// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text.Json;
using Foundry.Deploy.Services.Logging;

namespace Foundry.Deploy.Services.Deployment.Steps;

public sealed class FinalizeDeploymentAndWriteLogsStep : DeploymentStepBase
{
    public override string Name => DeploymentStepNames.FinalizeDeploymentAndWriteLogs;

    protected override Task<DeploymentStepResult> ExecuteLiveAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        return ExecuteFinalizeAsync(
            context,
            "Finalizing deployment artifacts.",
            "Deployment finalized.",
            cancellationToken);
    }

    protected override Task<DeploymentStepResult> ExecuteDryRunAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        return ExecuteFinalizeAsync(
            context,
            "[DRY-RUN] Finalize step completed.",
            "Deployment finalized (simulation).",
            cancellationToken);
    }

    private static async Task<DeploymentStepResult> ExecuteFinalizeAsync(
        DeploymentStepExecutionContext context,
        string stepLogMessage,
        string resultMessage,
        CancellationToken cancellationToken)
    {
        context.EmitCurrentStepIndeterminate("Finalizing deployment...", "Writing completion logs...", DeploymentOperationNames.WriteLogs);
        await context.AppendLogAsync(DeploymentLogLevel.Info, stepLogMessage, cancellationToken).ConfigureAwait(false);
        await context.AppendLogAsync(DeploymentLogLevel.Info, "[SUCCESS] Deployment orchestration completed.", cancellationToken).ConfigureAwait(false);

        context.EmitCurrentStepIndeterminate("Finalizing deployment...", "Writing deployment summary...", DeploymentOperationNames.WriteSummary);
        string summaryPath = await PersistFinalArtifactsAsync(context, cancellationToken).ConfigureAwait(false);
        context.RuntimeState.DeploymentSummaryPath = summaryPath;

        context.EmitCurrentStepIndeterminate("Finalizing deployment...", "Cleaning temporary workspace...", DeploymentOperationNames.CleanupWorkspace);
        CleanupTargetFoundryRoot(context.RuntimeState, context.LogSession);
        return DeploymentStepResult.Succeeded(resultMessage);
    }

    private static async Task<string> PersistFinalArtifactsAsync(
        DeploymentStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.RuntimeState.TargetWindowsPartitionRoot))
        {
            string transientRoot = context.RuntimeState.ResolvedCache?.RootPath
                ?? throw new InvalidOperationException("Cache strategy has not been resolved.");
            string summaryPath = Path.Combine(transientRoot, "State", "deployment-summary.json");
            await WriteDeploymentSummaryAsync(summaryPath, context.RuntimeState, cancellationToken).ConfigureAwait(false);
            return summaryPath;
        }

        string targetWindowsTempRoot = Path.Combine(context.RuntimeState.TargetWindowsPartitionRoot, "Windows", "Temp", "Foundry");
        await context.RebindLogSessionToTargetAsync(targetWindowsTempRoot, cancellationToken).ConfigureAwait(false);

        string finalSummaryPath = Path.Combine(targetWindowsTempRoot, "deployment-summary.json");
        await WriteDeploymentSummaryAsync(finalSummaryPath, context.RuntimeState, cancellationToken).ConfigureAwait(false);
        return finalSummaryPath;
    }

    internal static async Task WriteDeploymentSummaryAsync(
        string path,
        DeploymentRuntimeState runtimeState,
        CancellationToken cancellationToken)
    {
        string directoryPath = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Invalid deployment summary path '{path}'.");
        Directory.CreateDirectory(directoryPath);

        string json = JsonSerializer.Serialize(new
        {
            completedAtUtc = DateTimeOffset.UtcNow,
            mode = runtimeState.Mode.ToString(),
            isDryRun = runtimeState.IsDryRun,
            targetDiskNumber = runtimeState.TargetDiskNumber,
            targetComputerName = runtimeState.TargetComputerName,
            operatingSystemFileName = runtimeState.OperatingSystemFileName,
            operatingSystemUrl = runtimeState.OperatingSystemUrl,
            downloadedOperatingSystemPath = runtimeState.DownloadedOperatingSystemPath,
            downloadedDriverPackPath = runtimeState.DownloadedDriverPackPath,
            driverPackInstallMode = runtimeState.DriverPackInstallMode.ToString(),
            driverPackExtractionMethod = runtimeState.DriverPackExtractionMethod,
            extractedDriverPackPath = runtimeState.ExtractedDriverPackPath,
            deferredDriverPackagePath = runtimeState.DeferredDriverPackagePath,
            preOobeSetupCompletePath = runtimeState.PreOobeSetupCompletePath,
            preOobeRunnerPath = runtimeState.PreOobeRunnerPath,
            preOobeManifestPath = runtimeState.PreOobeManifestPath,
            preOobeScriptPaths = runtimeState.PreOobeScriptPaths,
            applyFirmwareUpdates = runtimeState.ApplyFirmwareUpdates,
            downloadedFirmwarePath = runtimeState.DownloadedFirmwarePath,
            extractedFirmwarePath = runtimeState.ExtractedFirmwarePath,
            firmwareUpdateId = runtimeState.FirmwareUpdateId,
            firmwareUpdateTitle = runtimeState.FirmwareUpdateTitle,
            autopilotEnabled = runtimeState.IsAutopilotEnabled,
            autopilotProvisioningMode = runtimeState.AutopilotProvisioningMode.ToString(),
            selectedAutopilotProfileFolderName = runtimeState.SelectedAutopilotProfileFolderName,
            selectedAutopilotProfileDisplayName = runtimeState.SelectedAutopilotProfileDisplayName,
            autopilotHardwareHashGroupTag = runtimeState.AutopilotHardwareHashGroupTag,
            autopilotHardwareHashUploadState = runtimeState.AutopilotHardwareHashUploadState.ToString(),
            autopilotHardwareHashUploadMessage = runtimeState.AutopilotHardwareHashUploadMessage,
            autopilotHardwareHashDiagnosticsPath = runtimeState.AutopilotHardwareHashDiagnosticsPath,
            targetSystemPartitionRoot = runtimeState.TargetSystemPartitionRoot,
            targetWindowsPartitionRoot = runtimeState.TargetWindowsPartitionRoot,
            targetRecoveryPartitionRoot = runtimeState.TargetRecoveryPartitionRoot,
            winReConfigured = runtimeState.WinReConfigured,
            stagedAutopilotConfigurationPath = runtimeState.StagedAutopilotConfigurationPath,
            completedSteps = runtimeState.CompletedSteps
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(path, VolumePathDiagnostics.Redact(json), cancellationToken).ConfigureAwait(false);
    }

    private static void CleanupTargetFoundryRoot(DeploymentRuntimeState runtimeState, DeploymentLogSession? logSession)
    {
        if (string.IsNullOrWhiteSpace(runtimeState.TargetFoundryRoot) ||
            string.IsNullOrWhiteSpace(runtimeState.TargetWindowsPartitionRoot))
        {
            return;
        }

        string finalRoot = Path.Combine(runtimeState.TargetWindowsPartitionRoot, "Windows", "Temp", "Foundry");
        if (runtimeState.TargetFoundryRoot.Equals(finalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (logSession is not null &&
            logSession.RootPath.Equals(runtimeState.TargetFoundryRoot, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDirectory(runtimeState.TargetFoundryRoot);
            return;
        }

        TryDeleteDirectory(runtimeState.TargetFoundryRoot);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
