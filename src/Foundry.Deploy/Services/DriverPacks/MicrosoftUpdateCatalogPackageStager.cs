// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.Networking;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.DriverPacks;

/// <summary>Validates complete catalog updates in isolated workspaces before publishing expanded payloads.</summary>
internal sealed class MicrosoftUpdateCatalogPackageStager
{
    private readonly IArchiveExtractionService _archiveExtractionService;
    private readonly IMicrosoftUpdateCatalogClient _catalogClient;
    private readonly IArtifactDownloadService _artifactDownloadService;
    private readonly PayloadCachePlacementService _placement;
    private readonly ILogger _logger;

    internal MicrosoftUpdateCatalogPackageStager(IArchiveExtractionService archiveExtractionService, IMicrosoftUpdateCatalogClient catalogClient,
        IArtifactDownloadService artifactDownloadService, PayloadCachePlacementService placement, ILogger logger)
    {
        _archiveExtractionService = archiveExtractionService;
        _catalogClient = catalogClient;
        _artifactDownloadService = artifactDownloadService;
        _placement = placement;
        _logger = logger;
    }
    internal async Task<IReadOnlyList<MicrosoftUpdateCatalogDownload>?> TryStageAsync(
        MicrosoftUpdateCatalogUpdate update, OperatingSystemCatalogItem target, IReadOnlyCollection<string> hardwareIds,
        string cacheDirectory, string stagingRoot, string consumerRoot, bool requireFirmware,
        ISet<string> publishedDirectories, CancellationToken cancellationToken)
    {
        IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads;
        ArtifactIdentity[] artifacts;
        MicrosoftUpdateCatalogDownload[] candidates;
        try
        {
            downloads = await _catalogClient.GetDownloadsAsync(update.UpdateId, cancellationToken).ConfigureAwait(false);
            candidates = MicrosoftUpdateCatalogSupport.GetCabCandidates(downloads, target.Architecture);
            if (candidates.Length == 0) return null;
            artifacts = candidates.Select(download => ArtifactIntegrityPolicy.FromMicrosoftUpdate(update, download,
                requireFirmware ? "MicrosoftUpdateCatalogFirmware" : "MicrosoftUpdateCatalogDriver")).ToArray();
            if (artifacts.GroupBy(artifact => artifact.SourceUri.AbsoluteUri, StringComparer.Ordinal)
                .Any(group => group.Select(artifact => artifact.CacheKey).Distinct(StringComparer.Ordinal).Count() > 1))
            {
                return null;
            }
            var unique = candidates.Zip(artifacts).DistinctBy(pair => pair.Second.CacheKey, StringComparer.Ordinal).ToArray();
            candidates = unique.Select(pair => pair.First).ToArray();
            artifacts = unique.Select(pair => pair.Second).ToArray();
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or UriFormatException)
        {
            _logger.LogInformation("Microsoft Update Catalog candidate has unusable package metadata; continuing lookup.");
            return null;
        }

        string workspaceParent = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + ".candidates";
        string workspace = Path.Combine(workspaceParent, Guid.NewGuid().ToString("N"));
        string payloadRoot = Path.Combine(workspace, "payload");
        bool retainWorkspace = false;
        Exception? operationError = null;
        try
        {
            Directory.CreateDirectory(payloadRoot);
            foreach (ArtifactIdentity artifact in artifacts)
            {
                string sourceFolder = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifact.SourceId)));
                string fallbackCacheRoot = Path.Combine(MicrosoftUpdateCatalogSupport.ResolveFallbackCacheRoot(stagingRoot), sourceFolder);
                PayloadCachePlacement placement = await _placement.ResolveAsync(artifact, Path.Combine(cacheDirectory, sourceFolder),
                    fallbackCacheRoot, cancellationToken).ConfigureAwait(false);
                ArtifactDownloadResult result = placement.CachedArtifact ??
                    await _artifactDownloadService.DownloadAsync(artifact, placement.Path, cancellationToken).ConfigureAwait(false);
                string inputDirectory = Path.Combine(workspace, "input", artifact.CacheKey);
                Directory.CreateDirectory(inputDirectory);
                string stagedPath = Path.Combine(inputDirectory, artifact.FileName);
                File.Copy(result.DestinationPath, stagedPath);
                using NativeFileLease lease = NativeFileLease.OpenRead(stagedPath);
                await VerifyStagedBytesAsync(stagedPath, artifact, cancellationToken).ConfigureAwait(false);
                string expandedPath = Path.Combine(payloadRoot, artifact.CacheKey);
                await lease.RunAsync(async () =>
                {
                    await _archiveExtractionService.ExtractWithSevenZipAsync(stagedPath, expandedPath, workspace, cancellationToken).ConfigureAwait(false);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
                if (!await ContainsApplicableInfAsync(expandedPath, target, hardwareIds, requireFirmware, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
            }
            string packageKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", artifacts.Select(artifact => artifact.CacheKey).Order(StringComparer.Ordinal)))));
            string destination = Path.Combine(consumerRoot, packageKey);
            Directory.CreateDirectory(consumerRoot);
            if (Directory.Exists(destination))
            {
                if (!publishedDirectories.Contains(destination))
                {
                    throw new IOException("Microsoft Update Catalog publication encountered an unowned expanded directory.");
                }
            }
            else
            {
                Directory.Move(payloadRoot, destination);
                publishedDirectories.Add(destination);
            }
            return candidates;
        }
        catch (Exception error)
        {
            operationError = error;
            retainWorkspace = NativeFileLease.HasRetainedProtection(error);
            throw;
        }
        finally
        {
            if (!retainWorkspace && Directory.Exists(workspace))
            {
                try
                {
                    Directory.Delete(workspace, recursive: true);
                }
                catch (Exception cleanupError) when (operationError is not null && cleanupError is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Microsoft Update Catalog candidate cleanup failed after an operation error. CleanupErrorType={ErrorType}", cleanupError.GetType().Name);
                }
            }
        }
    }

    private static async Task VerifyStagedBytesAsync(string path, ArtifactIdentity artifact, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        FileDigest digest = artifact.Integrity.Digest!;
        byte[] actual = digest.Algorithm == HashAlgorithmName.SHA256
            ? await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)
            : await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if ((artifact.Integrity.SizeBytes is long size && stream.Length != size) ||
            !CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(digest.Hex)))
        {
            throw new InvalidDataException("Microsoft Update Catalog staged payload failed integrity verification.");
        }
    }

    private static async Task<bool> ContainsApplicableInfAsync(string directory, OperatingSystemCatalogItem target,
        IReadOnlyCollection<string> hardwareIds, bool requireFirmware, CancellationToken cancellationToken)
    {
        const long maximumInfBytes = 4 * 1024 * 1024;
        const long maximumTotalInfBytes = 16 * 1024 * 1024;
        var pending = new Stack<string>();
        pending.Push(directory);
        int entryCount = 0;
        long totalInfBytes = 0;
        bool applicable = false;
        while (pending.TryPop(out string? current))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if (++entryCount > 16384 || (attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                if (Path.GetExtension(entry).Equals(".cab", StringComparison.OrdinalIgnoreCase)) return false;
                if (!Path.GetExtension(entry).Equals(".inf", StringComparison.OrdinalIgnoreCase)) continue;
                await using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                totalInfBytes += stream.Length;
                if (stream.Length > maximumInfBytes || totalInfBytes > maximumTotalInfBytes) return false;
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                string content;
                try { content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false); }
                catch (DecoderFallbackException) { return false; }
                applicable |= MicrosoftUpdateCatalogInfApplicability.IsApplicable(content, target, hardwareIds, requireFirmware);
            }
        }
        return applicable;
    }

}
