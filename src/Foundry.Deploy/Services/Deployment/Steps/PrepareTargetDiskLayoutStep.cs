// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Logging;

namespace Foundry.Deploy.Services.Deployment.Steps;

public sealed class PrepareTargetDiskLayoutStep : DeploymentStepBase
{
    private readonly IWindowsDeploymentService _windowsDeploymentService;

    public PrepareTargetDiskLayoutStep(IWindowsDeploymentService windowsDeploymentService)
    {
        _windowsDeploymentService = windowsDeploymentService;
    }

    public override string Name => DeploymentStepNames.PrepareTargetDiskLayout;

    protected override async Task<DeploymentStepResult> ExecuteLiveAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.RuntimeState.HardwareProfile?.FirmwareType != Foundry.Utilities.Hardware.WindowsFirmwareType.Uefi)
        {
            return DeploymentStepResult.Failed("Deployment requires UEFI boot mode.",
                DeploymentFailure.Guard(DeploymentOperationNames.ValidateTarget, DeploymentFailureReasons.InvalidState, "unsupported_boot_firmware"));
        }

        string workingDirectory = context.ResolveWorkspaceTempPath("Deployment");
        Directory.CreateDirectory(workingDirectory);

        context.EmitCurrentStepIndeterminate(
            "Preparing target disk layout...",
            "Revalidating target disk...",
            DeploymentOperationNames.ValidateTargetDisk);
        (_, DeploymentStepResult? validationFailure) = await context.TryGetValidatedTargetDiskAsync(cancellationToken).ConfigureAwait(false);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        context.EmitCurrentStepIndeterminate(
            "Preparing target disk layout...",
            "Partitioning target disk...",
            DeploymentOperationNames.PartitionTargetDisk);
        DeploymentTargetLayout layout = await _windowsDeploymentService
            .PrepareTargetDiskAsync(
                context.Request.ConfirmedTargetDisk!,
                workingDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        context.RuntimeState.TargetLayout = layout;
        context.RuntimeState.TargetSystemPartitionRoot = layout.SystemPartitionRoot;
        context.RuntimeState.TargetWindowsPartitionRoot = layout.WindowsPartitionRoot;
        context.RuntimeState.TargetRecoveryPartitionRoot = layout.RecoveryPartitionRoot;
        context.RuntimeState.TargetRecoveryPartitionLetter = layout.RecoveryPartitionLetter;
        context.RuntimeState.TargetFoundryRoot = Path.Combine(layout.WindowsPartitionRoot, "Foundry");

        context.EmitCurrentStepIndeterminate(
            "Preparing target disk layout...",
            "Preparing target workspace...",
            DeploymentOperationNames.PrepareTargetWorkspace);
        await context.RebindLogSessionToTargetAsync(context.RuntimeState.TargetFoundryRoot, cancellationToken).ConfigureAwait(false);

        await context.AppendLogAsync(
            DeploymentLogLevel.Info,
            $"Target disk prepared: system='{layout.SystemPartitionRoot}', windows='{layout.WindowsPartitionRoot}', recovery='{layout.RecoveryPartitionRoot}'.",
            cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded("Target disk layout prepared.");
    }

    protected override async Task<DeploymentStepResult> ExecuteDryRunAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        string targetRoot = context.ResolveWorkspaceTempPath("DryRunTarget");
        string systemRoot = Path.Combine(targetRoot, "System");
        string windowsRoot = Path.Combine(targetRoot, "Windows");
        string recoveryRoot = Path.Combine(targetRoot, "Recovery");

        context.EmitCurrentStepIndeterminate(
            "Preparing target disk layout...",
            "Creating simulated partitions...",
            DeploymentOperationNames.PartitionTargetDisk);
        Directory.CreateDirectory(systemRoot);
        Directory.CreateDirectory(windowsRoot);
        Directory.CreateDirectory(recoveryRoot);

        context.RuntimeState.TargetSystemPartitionRoot = systemRoot;
        context.RuntimeState.TargetWindowsPartitionRoot = windowsRoot;
        context.RuntimeState.TargetRecoveryPartitionRoot = recoveryRoot;
        context.RuntimeState.TargetRecoveryPartitionLetter = 'R';
        context.RuntimeState.TargetFoundryRoot = Path.Combine(windowsRoot, "Foundry");

        context.EmitCurrentStepIndeterminate(
            "Preparing target disk layout...",
            "Preparing target workspace...",
            DeploymentOperationNames.PrepareTargetWorkspace);
        await context.RebindLogSessionToTargetAsync(context.RuntimeState.TargetFoundryRoot, cancellationToken).ConfigureAwait(false);

        await context.AppendLogAsync(
            DeploymentLogLevel.Info,
            $"[DRY-RUN] Simulated target disk layout: system='{systemRoot}', windows='{windowsRoot}', recovery='{recoveryRoot}'.",
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded("Target disk layout prepared (simulation).");
    }
}
