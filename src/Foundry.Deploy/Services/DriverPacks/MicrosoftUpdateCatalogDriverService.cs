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

public sealed class MicrosoftUpdateCatalogDriverService : IMicrosoftUpdateCatalogDriverService
{
    private const string FirmwareClassGuid = "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}";
    private static readonly string[] CriticalPnpClasses =
    [
        "DiskDrive",
        "Net",
        "SCSIAdapter"
    ];

    private readonly IArchiveExtractionService _archiveExtractionService;
    private readonly IMicrosoftUpdateCatalogClient _catalogClient;
    private readonly IArtifactDownloadService _artifactDownloadService;
    private readonly PayloadCachePlacementService _placement;
    private readonly ILogger<MicrosoftUpdateCatalogDriverService> _logger;

    public MicrosoftUpdateCatalogDriverService(
        IArchiveExtractionService archiveExtractionService,
        IMicrosoftUpdateCatalogClient catalogClient,
        IArtifactDownloadService artifactDownloadService,
        ILogger<MicrosoftUpdateCatalogDriverService> logger,
        PayloadCachePlacementService? placement = null)
    {
        _archiveExtractionService = archiveExtractionService;
        _catalogClient = catalogClient;
        _artifactDownloadService = artifactDownloadService;
        _placement = placement ?? new PayloadCachePlacementService(artifactDownloadService, new VolumeStorageProbe());
        _logger = logger;
    }

    public async Task<MicrosoftUpdateCatalogDriverResult> DownloadAsync(
        HardwareProfile hardwareProfile,
        OperatingSystemCatalogItem operatingSystem,
        string destinationDirectory,
        string cacheDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(hardwareProfile);
        ArgumentNullException.ThrowIfNull(operatingSystem);

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("Destination directory is required.", nameof(destinationDirectory));
        }

        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new ArgumentException("Cache directory is required.", nameof(cacheDirectory));
        }

        DirectoryOperations.Recreate(destinationDirectory);
        progress?.Report(5d);

        DriverSearchTarget[] searchTargets = BuildSearchTargets(hardwareProfile);
        if (searchTargets.Length == 0)
        {
            progress?.Report(100d);
            return new MicrosoftUpdateCatalogDriverResult
            {
                DestinationDirectory = destinationDirectory,
                IsPayloadAvailable = false,
                InfCount = 0,
                DownloadedDrivers = Array.Empty<MicrosoftUpdateCatalogDownloadedDriver>(),
                Message = "No eligible critical Plug and Play devices (DiskDrive, Net, SCSIAdapter) were found for Microsoft Update Catalog driver lookup."
            };
        }

        if (!await _catalogClient.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(100d);
            return new MicrosoftUpdateCatalogDriverResult
            {
                DestinationDirectory = destinationDirectory,
                IsPayloadAvailable = false,
                InfCount = 0,
                DownloadedDrivers = Array.Empty<MicrosoftUpdateCatalogDownloadedDriver>(),
                Message = "Microsoft Update Catalog is not reachable; skipping driver lookup."
            };
        }

        progress?.Report(15d);
        string[] releaseSearchOrder = MicrosoftUpdateCatalogSupport.BuildReleaseSearchOrder(operatingSystem.ReleaseId);
        var matchedUpdates = new Dictionary<string, CatalogDownloadCandidate>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < searchTargets.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DriverSearchTarget searchTarget = searchTargets[index];
            CatalogDownloadCandidate? candidate = await FindCandidateAsync(
                    searchTarget,
                    releaseSearchOrder,
                    operatingSystem.Architecture,
                    cancellationToken)
                .ConfigureAwait(false);

            if (candidate is not null)
            {
                matchedUpdates.TryAdd(candidate.Update.UpdateId, candidate);
            }

            progress?.Report(15d + (double)(index + 1) / searchTargets.Length * 45d);
        }

        if (matchedUpdates.Count == 0)
        {
            progress?.Report(100d);
            return new MicrosoftUpdateCatalogDriverResult
            {
                DestinationDirectory = destinationDirectory,
                IsPayloadAvailable = false,
                InfCount = 0,
                DownloadedDrivers = Array.Empty<MicrosoftUpdateCatalogDownloadedDriver>(),
                Message = "Microsoft Update Catalog did not return any applicable driver payloads for the detected critical devices (DiskDrive, Net, SCSIAdapter)."
            };
        }

        int downloadIndex = 0;
        List<MicrosoftUpdateCatalogDownloadedDriver> downloadedDrivers = [];
        foreach (CatalogDownloadCandidate candidate in matchedUpdates.Values.OrderBy(static item => item.Update.Title, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string updateDirectory = Path.Combine(destinationDirectory, MicrosoftUpdateCatalogSupport.SanitizePathSegment(candidate.Update.UpdateId));
            Directory.CreateDirectory(updateDirectory);

            string fileName = ResolveFileName(candidate.Download);
            string destinationPath = Path.Combine(updateDirectory, fileName);

            await DownloadToStagingAsync(
                    candidate,
                    destinationPath,
                    cacheDirectory,
                    destinationDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            downloadedDrivers.Add(new MicrosoftUpdateCatalogDownloadedDriver
            {
                UpdateId = candidate.Update.UpdateId,
                Title = candidate.Update.Title,
                Version = candidate.Update.Version,
                Size = candidate.Update.Size,
                DownloadUrl = candidate.Download.DownloadUrl
            });

            downloadIndex++;
            progress?.Report(60d + (double)downloadIndex / matchedUpdates.Count * 40d);
        }

        int cabCount = Directory.EnumerateFiles(destinationDirectory, "*.cab", SearchOption.AllDirectories).Count();
        int infCount = Directory.EnumerateFiles(destinationDirectory, "*.inf", SearchOption.AllDirectories).Count();
        progress?.Report(100d);

        return new MicrosoftUpdateCatalogDriverResult
        {
            DestinationDirectory = destinationDirectory,
            IsPayloadAvailable = cabCount > 0 || infCount > 0,
            InfCount = infCount,
            DownloadedDrivers = downloadedDrivers,
            Message = cabCount > 0
                ? $"Microsoft Update Catalog payload downloaded: {cabCount} CAB files across {matchedUpdates.Count} updates."
                : infCount > 0
                    ? $"Microsoft Update Catalog payload resolved directly as INF content: {infCount} INF files across {matchedUpdates.Count} updates."
                    : "Microsoft Update Catalog returned updates, but no CAB or INF files were downloaded."
        };
    }

    public async Task<MicrosoftUpdateCatalogDriverResult> ExpandAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory is required.", nameof(sourceDirectory));
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("Destination directory is required.", nameof(destinationDirectory));
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Microsoft Update Catalog source directory '{sourceDirectory}' was not found.");
        }

        progress?.Report(5d);
        Directory.CreateDirectory(destinationDirectory);

        string[] cabFiles = Directory
            .EnumerateFiles(sourceDirectory, "*.cab", SearchOption.AllDirectories)
            .ToArray();

        if (cabFiles.Length == 0)
        {
            int existingInfCount = Directory
                .EnumerateFiles(sourceDirectory, "*.inf", SearchOption.AllDirectories)
                .Count();
            progress?.Report(100d);

            return new MicrosoftUpdateCatalogDriverResult
            {
                DestinationDirectory = existingInfCount > 0 ? sourceDirectory : destinationDirectory,
                IsPayloadAvailable = existingInfCount > 0,
                InfCount = existingInfCount,
                DownloadedDrivers = Array.Empty<MicrosoftUpdateCatalogDownloadedDriver>(),
                Message = existingInfCount > 0
                    ? $"Microsoft Update Catalog payload is already expanded: {existingInfCount} INF files."
                    : "Microsoft Update Catalog expand completed, but no CAB or INF files were found."
            };
        }

        for (int index = 0; index < cabFiles.Length; index++)
        {
            string cabPath = cabFiles[index];
            string folderName = ResolveExpandedFolderName(cabPath, sourceDirectory);
            string cabDestination = Path.Combine(destinationDirectory, MicrosoftUpdateCatalogSupport.SanitizePathSegment(folderName));
            Directory.CreateDirectory(cabDestination);

            double rangeStart = 10d + (double)index / cabFiles.Length * 85d;
            double rangeEnd = 10d + (double)(index + 1) / cabFiles.Length * 85d;
            await _archiveExtractionService
                .ExtractWithSevenZipAsync(
                    cabPath,
                    cabDestination,
                    destinationDirectory,
                    cancellationToken,
                    CreateMappedProgress(progress, rangeStart, rangeEnd))
                .ConfigureAwait(false);
        }

        int infCount = Directory
            .EnumerateFiles(destinationDirectory, "*.inf", SearchOption.AllDirectories)
            .Count();
        progress?.Report(100d);

        return new MicrosoftUpdateCatalogDriverResult
        {
            DestinationDirectory = destinationDirectory,
            IsPayloadAvailable = infCount > 0,
            InfCount = infCount,
            DownloadedDrivers = Array.Empty<MicrosoftUpdateCatalogDownloadedDriver>(),
            Message = infCount > 0
                ? $"Microsoft Update Catalog payload expanded: {infCount} INF files from {cabFiles.Length} CAB files."
                : $"Microsoft Update Catalog payload expanded from {cabFiles.Length} CAB files, but no INF files were found."
        };
    }

    private async Task<CatalogDownloadCandidate?> FindCandidateAsync(
        DriverSearchTarget searchTarget,
        IReadOnlyList<string> releaseSearchOrder,
        string targetArchitecture,
        CancellationToken cancellationToken)
    {
        MicrosoftUpdateCatalogUpdate? update = await SearchByReleaseAsync(
                searchTarget.NormalizedHardwareId,
                releaseSearchOrder,
                cancellationToken)
            .ConfigureAwait(false);

        if (update is null)
        {
            update = await SearchByRawHardwareIdAsync(searchTarget.RawFallbackTerms, cancellationToken).ConfigureAwait(false);
        }

        if (update is null)
        {
            _logger.LogDebug("No Microsoft Update Catalog match found for device '{DeviceName}'.", searchTarget.DeviceName);
            return null;
        }

        IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads = await _catalogClient
            .GetDownloadsAsync(update.UpdateId, cancellationToken)
            .ConfigureAwait(false);

        MicrosoftUpdateCatalogDownload? selectedDownload = MicrosoftUpdateCatalogSupport.SelectPreferredCab(downloads, targetArchitecture);
        if (selectedDownload is null)
        {
            _logger.LogInformation(
                "Microsoft Update Catalog update '{Title}' ({UpdateId}) has no CAB payload compatible with architecture '{Architecture}'.",
                update.Title,
                update.UpdateId,
                MicrosoftUpdateCatalogSupport.NormalizeArchitecture(targetArchitecture));
            return null;
        }

        return new CatalogDownloadCandidate
        {
            Update = update,
            Download = selectedDownload
        };
    }

    private async Task DownloadToStagingAsync(
        CatalogDownloadCandidate candidate,
        string destinationPath,
        string cacheDirectory,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        ArtifactIdentity artifact = ArtifactIntegrityPolicy.FromMicrosoftUpdate(candidate.Update, candidate.Download, "MicrosoftUpdateCatalogDriver");
        string fallbackCacheRoot = Path.Combine(MicrosoftUpdateCatalogSupport.ResolveFallbackCacheRoot(stagingRoot), artifact.SourceId);
        PayloadCachePlacement placement = await _placement.ResolveAsync(artifact, Path.Combine(cacheDirectory, artifact.SourceId),
            fallbackCacheRoot, cancellationToken).ConfigureAwait(false);
        ArtifactDownloadResult result = placement.CachedArtifact ?? await _artifactDownloadService.DownloadAsync(artifact, placement.Path, cancellationToken).ConfigureAwait(false);
        CopyFile(result.DestinationPath, destinationPath);
    }

    private async Task<MicrosoftUpdateCatalogUpdate?> SearchByReleaseAsync(
        string normalizedHardwareId,
        IReadOnlyList<string> releaseSearchOrder,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedHardwareId))
        {
            return null;
        }

        foreach (string releaseId in releaseSearchOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string query = MicrosoftUpdateCatalogSupport.BuildSearchQuery(releaseId, normalizedHardwareId);
            IReadOnlyList<MicrosoftUpdateCatalogUpdate> results = await _catalogClient
                .SearchAsync(query, descending: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            MicrosoftUpdateCatalogUpdate? update = results.FirstOrDefault();
            if (update is not null)
            {
                _logger.LogInformation(
                    "Found Microsoft Update Catalog match. ReleaseId={ReleaseId}, HardwareId={HardwareId}, UpdateId={UpdateId}, Title={Title}",
                    releaseId,
                    normalizedHardwareId,
                    update.UpdateId,
                    update.Title);
                return update;
            }
        }

        return null;
    }

    private async Task<MicrosoftUpdateCatalogUpdate?> SearchByRawHardwareIdAsync(
        IReadOnlyList<string> rawFallbackTerms,
        CancellationToken cancellationToken)
    {
        foreach (string rawHardwareId in rawFallbackTerms)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<MicrosoftUpdateCatalogUpdate> results = await _catalogClient
                .SearchAsync(rawHardwareId, descending: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            MicrosoftUpdateCatalogUpdate? update = results.FirstOrDefault();
            if (update is not null)
            {
                _logger.LogInformation(
                    "Found Microsoft Update Catalog fallback match. RawHardwareId={RawHardwareId}, UpdateId={UpdateId}, Title={Title}",
                    rawHardwareId,
                    update.UpdateId,
                    update.Title);
                return update;
            }
        }

        return null;
    }

    private static DriverSearchTarget[] BuildSearchTargets(HardwareProfile hardwareProfile)
    {
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<DriverSearchTarget>();

        foreach (PnpDeviceInfo device in hardwareProfile.PnpDevices)
        {
            if (device.ClassGuid.Equals(FirmwareClassGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsCriticalCatalogDevice(device))
            {
                continue;
            }

            string normalizedHardwareId = MicrosoftUpdateCatalogSupport.TryExtractDriverSearchHardwareId(device) ?? string.Empty;
            string[] rawFallbackTerms = device.HardwareIds
                .Prepend(device.DeviceId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(value => !value.Equals(normalizedHardwareId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            string dedupeKey = !string.IsNullOrWhiteSpace(normalizedHardwareId)
                ? normalizedHardwareId
                : rawFallbackTerms.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dedupeKey) || !seenKeys.Add(dedupeKey))
            {
                continue;
            }

            targets.Add(new DriverSearchTarget
            {
                DeviceName = ResolveDeviceName(device),
                NormalizedHardwareId = normalizedHardwareId,
                RawFallbackTerms = rawFallbackTerms
            });
        }

        return targets.ToArray();
    }

    private static bool IsCriticalCatalogDevice(PnpDeviceInfo device)
    {
        string normalizedPnpClass = device.PnpClass.Trim();
        return CriticalPnpClasses.Contains(normalizedPnpClass, StringComparer.OrdinalIgnoreCase);
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

    private static string ResolveDeviceName(PnpDeviceInfo device)
    {
        if (!string.IsNullOrWhiteSpace(device.Name))
        {
            return device.Name.Trim();
        }

        return !string.IsNullOrWhiteSpace(device.DeviceId)
            ? device.DeviceId.Trim()
            : "Unknown device";
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

    private sealed record DriverSearchTarget
    {
        public required string DeviceName { get; init; }
        public required string NormalizedHardwareId { get; init; }
        public required IReadOnlyList<string> RawFallbackTerms { get; init; }
    }

    private sealed record CatalogDownloadCandidate
    {
        public required MicrosoftUpdateCatalogUpdate Update { get; init; }
        public required MicrosoftUpdateCatalogDownload Download { get; init; }
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
