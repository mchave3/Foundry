// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.IO;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.DriverPacks;

public sealed class MicrosoftUpdateCatalogDriverService : IMicrosoftUpdateCatalogDriverService
{
    private const string FirmwareClassGuid = "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}";
    private static readonly string[] CriticalPnpClasses = ["DiskDrive", "Net", "SCSIAdapter"];
    private readonly IArchiveExtractionService _archiveExtractionService;
    private readonly IMicrosoftUpdateCatalogClient _catalogClient;
    private readonly MicrosoftUpdateCatalogPackageStager _stager;

    public MicrosoftUpdateCatalogDriverService(IArchiveExtractionService archiveExtractionService, IMicrosoftUpdateCatalogClient catalogClient,
        IArtifactDownloadService artifactDownloadService, ILogger<MicrosoftUpdateCatalogDriverService> logger, PayloadCachePlacementService? placement = null)
    {
        _archiveExtractionService = archiveExtractionService;
        _catalogClient = catalogClient;
        _stager = new MicrosoftUpdateCatalogPackageStager(archiveExtractionService, catalogClient, artifactDownloadService,
            placement ?? new PayloadCachePlacementService(artifactDownloadService, new VolumeStorageProbe()), logger);
    }

    public async Task<MicrosoftUpdateCatalogDriverResult> DownloadAsync(HardwareProfile hardwareProfile, OperatingSystemCatalogItem operatingSystem,
        string destinationDirectory, string cacheDirectory, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(hardwareProfile);
        ArgumentNullException.ThrowIfNull(operatingSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        DirectoryOperations.Recreate(destinationDirectory);
        progress?.Report(5d);
        string[] releases = MicrosoftUpdateCatalogSupport.BuildReleaseSearchOrder(operatingSystem.ReleaseId);
        string[][] targets = hardwareProfile.PnpDevices
            .Where(device => !device.ClassGuid.Equals(FirmwareClassGuid, StringComparison.OrdinalIgnoreCase) &&
                CriticalPnpClasses.Contains(device.PnpClass.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(MicrosoftUpdateCatalogSupport.BuildHardwareSearchOrder).Where(ids => ids.Length > 0)
            .DistinctBy(ids => ids[0], StringComparer.OrdinalIgnoreCase).ToArray();
        var downloaded = new Dictionary<string, MicrosoftUpdateCatalogDownloadedDriver>(StringComparer.Ordinal);
        var publishedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < targets.Length; index++)
        {
            if (releases.Length == 0 || operatingSystem.BuildMajor <= 0) break;
            await FindAndStageDriverAsync(targets[index], operatingSystem, releases[0], destinationDirectory, cacheDirectory,
                downloaded, publishedDirectories, cancellationToken).ConfigureAwait(false);
            progress?.Report(5d + (double)(index + 1) / targets.Length * 90d);
        }
        int infCount = Directory.EnumerateFiles(destinationDirectory, "*.inf", SearchOption.AllDirectories).Count();
        progress?.Report(100d);
        return new MicrosoftUpdateCatalogDriverResult
        {
            DestinationDirectory = destinationDirectory,
            IsPayloadAvailable = infCount > 0,
            InfCount = infCount,
            DownloadedDrivers = downloaded.Values.ToArray(),
            Message = infCount > 0 ? $"Microsoft Update Catalog prepared {infCount} driver INF files from applicable packages."
                : "Microsoft Update Catalog did not return applicable driver payloads for the detected critical devices."
        };
    }

    private async Task FindAndStageDriverAsync(string[] hardwareIds, OperatingSystemCatalogItem target, string release,
        string destinationDirectory, string cacheDirectory, Dictionary<string, MicrosoftUpdateCatalogDownloadedDriver> downloaded,
        ISet<string> publishedDirectories, CancellationToken cancellationToken)
    {
        foreach (string hardwareId in hardwareIds)
        {
            var seenUpdates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<MicrosoftUpdateCatalogUpdate> updates = await _catalogClient.SearchAsync(
                MicrosoftUpdateCatalogSupport.BuildSearchQuery(release, hardwareId), true, cancellationToken).ConfigureAwait(false);
            foreach (MicrosoftUpdateCatalogUpdate update in updates.OrderByDescending(update => update.LastUpdated))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenUpdates.Add(update.UpdateId) || !MicrosoftUpdateCatalogSupport.AllowsTargetRelease(update, target)) continue;
                IReadOnlyList<MicrosoftUpdateCatalogDownload>? staged = await _stager.TryStageAsync(update, target, [hardwareId],
                    cacheDirectory, destinationDirectory, destinationDirectory, false, publishedDirectories, cancellationToken).ConfigureAwait(false);
                if (staged is null) continue;
                foreach (MicrosoftUpdateCatalogDownload download in staged)
                {
                    downloaded.TryAdd(download.DownloadUrl, new MicrosoftUpdateCatalogDownloadedDriver
                    {
                        UpdateId = update.UpdateId,
                        Title = update.Title,
                        Version = update.Version,
                        Size = update.Size,
                        DownloadUrl = download.DownloadUrl
                    });
                }
                return;
            }
        }
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
