// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.IO;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.DriverPacks;

public sealed class MicrosoftUpdateCatalogFirmwareService : IMicrosoftUpdateCatalogFirmwareService
{
    private readonly IArchiveExtractionService _archiveExtractionService;
    private readonly IMicrosoftUpdateCatalogClient _catalogClient;
    private readonly IArtifactDownloadService _artifactDownloadService;
    private readonly PayloadCachePlacementService _placement;
    private readonly ILogger<MicrosoftUpdateCatalogFirmwareService> _logger;

    public MicrosoftUpdateCatalogFirmwareService(
        IArchiveExtractionService archiveExtractionService,
        IMicrosoftUpdateCatalogClient catalogClient,
        IArtifactDownloadService artifactDownloadService,
        ILogger<MicrosoftUpdateCatalogFirmwareService> logger,
        PayloadCachePlacementService? placement = null)
    {
        _archiveExtractionService = archiveExtractionService;
        _catalogClient = catalogClient;
        _artifactDownloadService = artifactDownloadService;
        _placement = placement ?? new PayloadCachePlacementService(artifactDownloadService, new VolumeStorageProbe());
        _logger = logger;
    }

    public async Task<MicrosoftUpdateCatalogFirmwareResult> DownloadAsync(
        HardwareProfile hardwareProfile,
        string targetArchitecture,
        string rawDirectory,
        string extractedDirectory,
        string cacheDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(hardwareProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        string firmwareHardwareId = hardwareProfile.SystemFirmwareHardwareId.Trim();
        if (string.IsNullOrWhiteSpace(firmwareHardwareId))
        {
            return new MicrosoftUpdateCatalogFirmwareResult
            {
                Message = "System firmware hardware identifier is unavailable; skipping firmware update lookup."
            };
        }

        DirectoryOperations.Recreate(rawDirectory);
        DirectoryOperations.Recreate(extractedDirectory);
        progress?.Report(5d);

        if (!await _catalogClient.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return new MicrosoftUpdateCatalogFirmwareResult
            {
                DownloadedDirectory = rawDirectory,
                ExtractedDirectory = extractedDirectory,
                Message = "Microsoft Update Catalog is not reachable; skipping firmware update."
            };
        }

        progress?.Report(20d);
        IReadOnlyList<MicrosoftUpdateCatalogUpdate> updates = await _catalogClient
            .SearchAsync(firmwareHardwareId, descending: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        MicrosoftUpdateCatalogUpdate? update = updates.FirstOrDefault();
        if (update is null)
        {
            return new MicrosoftUpdateCatalogFirmwareResult
            {
                DownloadedDirectory = rawDirectory,
                ExtractedDirectory = extractedDirectory,
                Message = $"No firmware update was found in Microsoft Update Catalog for firmware id '{firmwareHardwareId}'."
            };
        }

        progress?.Report(35d);
        IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads = await _catalogClient
            .GetDownloadsAsync(update.UpdateId, cancellationToken)
            .ConfigureAwait(false);

        MicrosoftUpdateCatalogDownload? selectedDownload = MicrosoftUpdateCatalogSupport.SelectPreferredCab(downloads, targetArchitecture);
        if (selectedDownload is null)
        {
            return new MicrosoftUpdateCatalogFirmwareResult
            {
                DownloadedDirectory = rawDirectory,
                ExtractedDirectory = extractedDirectory,
                UpdateId = update.UpdateId,
                Title = update.Title,
                Message = $"Firmware update '{update.Title}' was found, but no CAB payload was available for download."
            };
        }

        string updateDirectory = Path.Combine(rawDirectory, MicrosoftUpdateCatalogSupport.SanitizePathSegment(update.UpdateId));
        Directory.CreateDirectory(updateDirectory);

        string fileName = ResolveFileName(selectedDownload);
        string destinationPath = Path.Combine(updateDirectory, fileName);

        progress?.Report(50d);
        await DownloadToStagingAsync(update, selectedDownload, destinationPath, cacheDirectory, rawDirectory, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(75d);
        int infCount = await ExpandAsync(rawDirectory, extractedDirectory, cancellationToken, progress).ConfigureAwait(false);
        if (infCount == 0)
        {
            throw new InvalidOperationException(
                $"Firmware update '{update.Title}' was downloaded but no INF files were found after expansion.");
        }

        _logger.LogInformation(
            "Firmware update downloaded and expanded. UpdateId={UpdateId}, Title={Title}, InfCount={InfCount}",
            update.UpdateId,
            update.Title,
            infCount);

        return new MicrosoftUpdateCatalogFirmwareResult
        {
            IsUpdateAvailable = true,
            DownloadedDirectory = rawDirectory,
            ExtractedDirectory = extractedDirectory,
            UpdateId = update.UpdateId,
            Title = update.Title,
            Message = $"Firmware update prepared: {update.Title} ({infCount} INF files)."
        };
    }

    private async Task<int> ExpandAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        string[] cabFiles = Directory
            .EnumerateFiles(sourceDirectory, "*.cab", SearchOption.AllDirectories)
            .ToArray();

        if (cabFiles.Length == 0)
        {
            return 0;
        }

        for (int index = 0; index < cabFiles.Length; index++)
        {
            string cabPath = cabFiles[index];
            string folderName = ResolveExpandedFolderName(cabPath, sourceDirectory);
            string cabDestination = Path.Combine(destinationDirectory, MicrosoftUpdateCatalogSupport.SanitizePathSegment(folderName));
            Directory.CreateDirectory(cabDestination);

            double rangeStart = 75d + (double)index / cabFiles.Length * 25d;
            double rangeEnd = 75d + (double)(index + 1) / cabFiles.Length * 25d;
            await _archiveExtractionService
                .ExtractWithSevenZipAsync(
                    cabPath,
                    cabDestination,
                    destinationDirectory,
                    cancellationToken,
                    CreateMappedProgress(progress, rangeStart, rangeEnd))
                .ConfigureAwait(false);
        }

        return Directory.EnumerateFiles(destinationDirectory, "*.inf", SearchOption.AllDirectories).Count();
    }

    private async Task DownloadToStagingAsync(
        MicrosoftUpdateCatalogUpdate update,
        MicrosoftUpdateCatalogDownload download,
        string destinationPath,
        string cacheDirectory,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        ArtifactIdentity artifact = ArtifactIntegrityPolicy.FromMicrosoftUpdate(update, download, "MicrosoftUpdateCatalogFirmware");
        string fallbackCacheRoot = Path.Combine(MicrosoftUpdateCatalogSupport.ResolveFallbackCacheRoot(stagingRoot), artifact.SourceId);
        PayloadCachePlacement placement = await _placement.ResolveAsync(artifact, Path.Combine(cacheDirectory, artifact.SourceId),
            fallbackCacheRoot, cancellationToken).ConfigureAwait(false);
        ArtifactDownloadResult result = placement.CachedArtifact ?? await _artifactDownloadService.DownloadAsync(artifact, placement.Path, cancellationToken).ConfigureAwait(false);
        CopyFile(result.DestinationPath, destinationPath);
    }

    private static string ResolveFileName(MicrosoftUpdateCatalogDownload download)
    {
        return string.IsNullOrWhiteSpace(download.FileName)
            ? MicrosoftUpdateCatalogSupport.ResolveFileNameFromUrl(download.DownloadUrl)
            : MicrosoftUpdateCatalogSupport.SanitizePathSegment(download.FileName);
    }

    private static void CopyFile(string sourcePath, string destinationPath)
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static string ResolveExpandedFolderName(string cabPath, string sourceDirectory)
    {
        string parentFolder = Path.GetFileName(Path.GetDirectoryName(cabPath) ?? string.Empty);
        string sourceFolder = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return !string.IsNullOrWhiteSpace(parentFolder) &&
               !parentFolder.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase)
            ? parentFolder
            : Path.GetFileNameWithoutExtension(cabPath);
    }

    private static IProgress<double>? CreateMappedProgress(IProgress<double>? progress, double start, double end)
    {
        if (progress is null)
        {
            return null;
        }

        return new Progress<double>(percent =>
        {
            double normalized = Math.Clamp(percent, 0d, 100d);
            progress.Report(start + (normalized / 100d * (end - start)));
        });
    }
}
