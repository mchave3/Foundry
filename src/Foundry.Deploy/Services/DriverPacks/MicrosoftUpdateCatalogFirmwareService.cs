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

public sealed class MicrosoftUpdateCatalogFirmwareService : IMicrosoftUpdateCatalogFirmwareService
{
    private readonly IMicrosoftUpdateCatalogClient _catalogClient;
    private readonly MicrosoftUpdateCatalogPackageStager _stager;
    private readonly ILogger<MicrosoftUpdateCatalogFirmwareService> _logger;

    public MicrosoftUpdateCatalogFirmwareService(IArchiveExtractionService archiveExtractionService, IMicrosoftUpdateCatalogClient catalogClient,
        IArtifactDownloadService artifactDownloadService, ILogger<MicrosoftUpdateCatalogFirmwareService> logger, PayloadCachePlacementService? placement = null)
    {
        _catalogClient = catalogClient;
        _stager = new MicrosoftUpdateCatalogPackageStager(archiveExtractionService, catalogClient, artifactDownloadService,
            placement ?? new PayloadCachePlacementService(artifactDownloadService, new VolumeStorageProbe()), logger);
        _logger = logger;
    }

    public async Task<MicrosoftUpdateCatalogFirmwareResult> DownloadAsync(HardwareProfile hardwareProfile, OperatingSystemCatalogItem operatingSystem,
        string rawDirectory, string extractedDirectory, string cacheDirectory, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(hardwareProfile);
        ArgumentNullException.ThrowIfNull(operatingSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        DirectoryOperations.Recreate(rawDirectory);
        DirectoryOperations.Recreate(extractedDirectory);
        progress?.Report(5d);
        string firmwareId = hardwareProfile.SystemFirmwareHardwareId.Trim();
        string[] releases = MicrosoftUpdateCatalogSupport.BuildReleaseSearchOrder(operatingSystem.ReleaseId);
        var publishedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (firmwareId.StartsWith(@"UEFI\RES_", StringComparison.OrdinalIgnoreCase) && releases.Length > 0 && operatingSystem.BuildMajor > 0)
        {
            IReadOnlyList<MicrosoftUpdateCatalogUpdate> updates = await _catalogClient.SearchAsync(
                MicrosoftUpdateCatalogSupport.BuildSearchQuery(releases[0], firmwareId), true, cancellationToken).ConfigureAwait(false);
            foreach (MicrosoftUpdateCatalogUpdate update in updates.OrderByDescending(update => update.LastUpdated).DistinctBy(update => update.UpdateId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MicrosoftUpdateCatalogSupport.AllowsTargetRelease(update, operatingSystem)) continue;
                IReadOnlyList<MicrosoftUpdateCatalogDownload>? staged = await _stager.TryStageAsync(update, operatingSystem, [firmwareId],
                    cacheDirectory, rawDirectory, extractedDirectory, true, publishedDirectories, cancellationToken).ConfigureAwait(false);
                if (staged is null) continue;
                int infCount = Directory.EnumerateFiles(extractedDirectory, "*.inf", SearchOption.AllDirectories).Count();
                progress?.Report(100d);
                _logger.LogInformation("Microsoft Update Catalog firmware prepared. InfCount={InfCount}", infCount);
                return new MicrosoftUpdateCatalogFirmwareResult
                {
                    IsUpdateAvailable = true,
                    DownloadedDirectory = rawDirectory,
                    ExtractedDirectory = extractedDirectory,
                    UpdateId = update.UpdateId,
                    Title = update.Title,
                    Message = $"Microsoft Update Catalog prepared a compatible firmware package ({infCount} INF files)."
                };
            }
        }
        progress?.Report(100d);
        return new MicrosoftUpdateCatalogFirmwareResult
        {
            DownloadedDirectory = rawDirectory,
            ExtractedDirectory = extractedDirectory,
            Message = "Microsoft Update Catalog did not return an applicable system firmware package."
        };
    }
}
