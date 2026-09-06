// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.Logging;
using Foundry.Utilities.IO;

namespace Foundry.Deploy.Services.Deployment.Steps;

public sealed class DownloadDriverPackStep : DeploymentStepBase
{
    private readonly IMicrosoftUpdateCatalogDriverService _microsoftUpdateCatalogDriverService;
    private readonly IArtifactDownloadService _artifactDownloadService;
    private readonly PayloadCachePlacementService _placement;

    public DownloadDriverPackStep(
        IMicrosoftUpdateCatalogDriverService microsoftUpdateCatalogDriverService,
        IArtifactDownloadService artifactDownloadService,
        PayloadCachePlacementService? placement = null)
    {
        _microsoftUpdateCatalogDriverService = microsoftUpdateCatalogDriverService;
        _artifactDownloadService = artifactDownloadService;
        _placement = placement ?? new PayloadCachePlacementService(artifactDownloadService, new VolumeStorageProbe());
    }

    public override string Name => DeploymentStepNames.DownloadDriverPack;

    protected override async Task<DeploymentStepResult> ExecuteLiveAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        ResetDriverPackRuntimeState(context.RuntimeState);
        context.RuntimeState.DriverPackSelectionKind = context.Request.DriverPackSelectionKind;

        switch (context.Request.DriverPackSelectionKind)
        {
            case DriverPackSelectionKind.None:
                return DeploymentStepResult.Skipped("Driver pack disabled (None selected).");

            case DriverPackSelectionKind.MicrosoftUpdateCatalog:
            {
                HardwareProfile hardwareProfile = context.RuntimeState.HardwareProfile
                    ?? throw new InvalidOperationException("Hardware profile is unavailable for Microsoft Update Catalog lookup.");
                string rawDirectory = context.ResolveWorkspaceTempPath("DriverPack", "MicrosoftUpdateCatalog", "Raw");
                string cacheDirectory = context.ResolveMicrosoftUpdateCatalogDriverCacheRoot();
                DirectoryOperations.Recreate(rawDirectory);
                context.EmitCurrentStepIndeterminate("Downloading driver pack...", "Preparing download...", DeploymentOperationNames.ResolveDriverPack);
                IProgress<double> progress = context.CreateStepPercentProgressReporter("Downloading driver pack...", "Downloading");

                MicrosoftUpdateCatalogDriverResult result = await _microsoftUpdateCatalogDriverService
                    .DownloadAsync(hardwareProfile, context.Request.OperatingSystem, rawDirectory, cacheDirectory, cancellationToken, progress)
                    .ConfigureAwait(false);

                context.RuntimeState.DriverPackName = "Microsoft Update Catalog";
                context.RuntimeState.DriverPackUrl = null;
                await context.AppendLogAsync(DeploymentLogLevel.Info, result.Message, cancellationToken).ConfigureAwait(false);
                foreach (MicrosoftUpdateCatalogDownloadedDriver downloadedDriver in result.DownloadedDrivers)
                {
                    string sourceHost = Uri.TryCreate(downloadedDriver.DownloadUrl, UriKind.Absolute, out Uri? source)
                        ? source.Host : "unavailable";
                    string updateId = new(downloadedDriver.UpdateId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(64).ToArray());
                    await context.AppendLogAsync(
                        DeploymentLogLevel.Info,
                        $"Microsoft Update Catalog driver downloaded | UpdateId={updateId} | SourceHost={sourceHost}",
                        cancellationToken).ConfigureAwait(false);
                }

                if (!result.IsPayloadAvailable)
                {
                    context.RuntimeState.DownloadedDriverPackPath = null;
                    return DeploymentStepResult.Skipped(result.Message);
                }

                context.RuntimeState.DownloadedDriverPackPath = result.DestinationDirectory;
                return DeploymentStepResult.Succeeded("Driver pack downloaded.");
            }

            case DriverPackSelectionKind.OemCatalog:
            {
                DriverPackCatalogItem? driverPack = context.Request.DriverPack;
                if (driverPack is null)
                {
                    return DeploymentStepResult.Skipped("OEM driver pack mode selected but no driver pack was provided.");
                }

                context.RuntimeState.DriverPackName = driverPack.Name;
                context.RuntimeState.DriverPackUrl = driverPack.DownloadUrl;

                ArtifactIdentity artifact = ArtifactIntegrityPolicy.FromDriverPack(driverPack);
                string manufacturer = DeploymentStepExecutionContext.SanitizePathSegment(driverPack.Manufacturer);
                string? targetRoot = context.ResolveTargetPayloadCacheRoot("DriverPacks");
                context.EmitCurrentStepIndeterminate("Downloading driver pack...", "Checking cache...", DeploymentOperationNames.DownloadDriverPack);
                PayloadCachePlacement placement = await _placement.ResolveAsync(artifact,
                    Path.Combine(context.ResolveDriverPackCacheRoot(), manufacturer),
                    targetRoot is null ? null : Path.Combine(targetRoot, manufacturer), cancellationToken).ConfigureAwait(false);
                IProgress<DownloadProgress> driverPackDownloadProgress = context.CreateDownloadProgressReporter(
                    "Driver pack",
                    DeploymentOperationNames.DownloadDriverPack);

                ArtifactDownloadResult download = placement.CachedArtifact ?? await _artifactDownloadService
                    .DownloadAsync(
                        artifact,
                        placement.Path,
                        cancellationToken: cancellationToken,
                        progress: driverPackDownloadProgress)
                    .ConfigureAwait(false);

                context.RuntimeState.DownloadedDriverPackPath = download.DestinationPath;
                await context.AppendLogAsync(
                    DeploymentLogLevel.Info,
                    $"Driver pack {(download.Downloaded ? "downloaded" : "reused")} via {download.Method}: {download.DestinationPath}",
                    cancellationToken).ConfigureAwait(false);

                return DeploymentStepResult.Succeeded(
                    download.Downloaded
                        ? "Driver pack downloaded."
                        : "Driver pack resolved from cache.");
            }
        }

        return DeploymentStepResult.Skipped("No driver pack download required.");
    }

    protected override async Task<DeploymentStepResult> ExecuteDryRunAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        ResetDriverPackRuntimeState(context.RuntimeState);
        context.RuntimeState.DriverPackSelectionKind = context.Request.DriverPackSelectionKind;

        if (context.Request.DriverPackSelectionKind == DriverPackSelectionKind.None)
        {
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            return DeploymentStepResult.Skipped("Driver pack disabled (None selected).");
        }

        string downloadRoot = context.ResolveWorkspaceTempPath("DriverPack", "DryRun");
        Directory.CreateDirectory(downloadRoot);

        if (context.Request.DriverPackSelectionKind == DriverPackSelectionKind.MicrosoftUpdateCatalog)
        {
            string rawDirectory = Path.Combine(downloadRoot, "MicrosoftUpdateCatalog");
            Directory.CreateDirectory(rawDirectory);
            string cabPath = Path.Combine(rawDirectory, "driver.cab");
            await File.WriteAllTextAsync(cabPath, "dry-run", cancellationToken).ConfigureAwait(false);
            context.RuntimeState.DriverPackName = "Microsoft Update Catalog";
            context.RuntimeState.DownloadedDriverPackPath = rawDirectory;
            await context.AppendLogAsync(
                DeploymentLogLevel.Info,
                $"[DRY-RUN] Simulated Microsoft Update Catalog payload download: {rawDirectory}",
                cancellationToken).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            return DeploymentStepResult.Succeeded("Driver pack downloaded (simulation).");
        }

        DriverPackCatalogItem? driverPack = context.Request.DriverPack;
        if (driverPack is null)
        {
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            return DeploymentStepResult.Skipped("OEM driver pack mode selected but no driver pack was provided.");
        }

        string fileName = DeploymentStepExecutionContext.ResolveFileName(driverPack.FileName, driverPack.DownloadUrl);
        string simulatedPath = Path.Combine(downloadRoot, fileName);
        await File.WriteAllTextAsync(simulatedPath, "dry-run", cancellationToken).ConfigureAwait(false);

        context.RuntimeState.DriverPackName = driverPack.Name;
        context.RuntimeState.DriverPackUrl = driverPack.DownloadUrl;
        context.RuntimeState.DownloadedDriverPackPath = simulatedPath;

        await context.AppendLogAsync(
            DeploymentLogLevel.Info,
            $"[DRY-RUN] Simulated driver pack download: {simulatedPath}",
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded("Driver pack downloaded (simulation).");
    }

    private static void ResetDriverPackRuntimeState(DeploymentRuntimeState runtimeState)
    {
        runtimeState.DownloadedDriverPackPath = null;
        runtimeState.DriverPackName = null;
        runtimeState.DriverPackUrl = null;
        runtimeState.DriverPackInstallMode = DriverPackInstallMode.None;
        runtimeState.DriverPackExtractionMethod = null;
        runtimeState.ExtractedDriverPackPath = null;
        runtimeState.DeferredDriverPackagePath = null;
    }

}
