// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Logging;

namespace Foundry.Deploy.Services.Deployment.Steps;

public sealed class DownloadOperatingSystemImageStep : DeploymentStepBase
{
    private readonly IArtifactDownloadService _artifactDownloadService;
    private readonly PayloadCachePlacementService _placement;

    public DownloadOperatingSystemImageStep(IArtifactDownloadService artifactDownloadService, PayloadCachePlacementService? placement = null)
    {
        _artifactDownloadService = artifactDownloadService;
        _placement = placement ?? new PayloadCachePlacementService(artifactDownloadService, new VolumeStorageProbe());
    }

    public override string Name => DeploymentStepNames.DownloadOperatingSystemImage;

    protected override async Task<DeploymentStepResult> ExecuteLiveAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        ArtifactIdentity artifact = ArtifactIntegrityPolicy.FromOperatingSystem(context.Request.OperatingSystem);
        const string stepMessage = "Downloading OS image...";

        context.EmitCurrentStepIndeterminate(
            stepMessage,
            "Checking cache...",
            DeploymentOperationNames.DownloadOperatingSystemImage);
        PayloadCachePlacement placement = await _placement.ResolveAsync(artifact,
            context.ResolveOperatingSystemCacheRoot(), context.ResolveTargetPayloadCacheRoot("OperatingSystems"), cancellationToken).ConfigureAwait(false);
        IProgress<DownloadProgress> osDownloadProgress = context.CreateDownloadProgressReporter(
            "OS image",
            DeploymentOperationNames.DownloadOperatingSystemImage);
        ArtifactDownloadResult result = placement.CachedArtifact ?? await _artifactDownloadService
            .DownloadAsync(
                artifact,
                placement.Path,
                cancellationToken: cancellationToken,
                progress: osDownloadProgress)
            .ConfigureAwait(false);

        context.RuntimeState.DownloadedOperatingSystemPath = result.DestinationPath;
        await context.AppendLogAsync(
            DeploymentLogLevel.Info,
            $"OS image {(result.Downloaded ? "downloaded" : "reused")} via {result.Method}: {result.DestinationPath}",
            cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded(
            result.Downloaded
                ? "Operating system image downloaded."
                : "Operating system image resolved from cache.");
    }

    protected override async Task<DeploymentStepResult> ExecuteDryRunAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        string osDirectory = context.ResolveOperatingSystemCacheRoot();
        Directory.CreateDirectory(osDirectory);

        string fileName = DeploymentStepExecutionContext.ResolveFileName(
            context.Request.OperatingSystem.FileName,
            context.Request.OperatingSystem.Url);
        string simulatedPath = Path.Combine(osDirectory, $"{fileName}.dryrun.txt");
        await File.WriteAllTextAsync(
            simulatedPath,
            $"Dry-run artifact created at {DateTimeOffset.UtcNow:O}{Environment.NewLine}SourceHost={new Uri(context.Request.OperatingSystem.Url).Host}",
            cancellationToken).ConfigureAwait(false);

        context.RuntimeState.DownloadedOperatingSystemPath = simulatedPath;
        await context.AppendLogAsync(DeploymentLogLevel.Info, $"[DRY-RUN] Simulated OS artifact: {simulatedPath}", cancellationToken).ConfigureAwait(false);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded("Operating system image ready (simulation).");
    }
}
