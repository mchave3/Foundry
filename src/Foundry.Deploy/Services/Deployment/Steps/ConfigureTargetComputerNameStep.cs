// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Services.Logging;

namespace Foundry.Deploy.Services.Deployment.Steps;

public sealed class ConfigureTargetComputerNameStep : DeploymentStepBase
{
    private readonly IWindowsDeploymentService _windowsDeploymentService;

    public ConfigureTargetComputerNameStep(IWindowsDeploymentService windowsDeploymentService)
    {
        _windowsDeploymentService = windowsDeploymentService;
    }

    public override string Name => DeploymentStepNames.ConfigureTargetComputerName;

    protected override async Task<DeploymentStepResult> ExecuteLiveAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Request.UsesCustomUnattend)
        {
            return DeploymentStepResult.Skipped("Skipped because a custom answer file is selected.");
        }

        if (string.IsNullOrWhiteSpace(context.RuntimeState.TargetWindowsPartitionRoot))
        {
            return CreateMissingTargetPartitionFailure();
        }

        context.EmitCurrentStepIndeterminate("Configuring target computer name...", "Writing offline computer name...", DeploymentOperationNames.ConfigureComputerName);
        await _windowsDeploymentService
            .ConfigureOfflineComputerNameAsync(
                context.RuntimeState.TargetWindowsPartitionRoot,
                context.RuntimeState.TargetComputerName,
                context.Request.OperatingSystem.Architecture,
                context.Request.DefaultTimeZoneId,
                cancellationToken)
            .ConfigureAwait(false);

        await context.AppendLogAsync(
            DeploymentLogLevel.Info,
            "Target computer name configured.",
            cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded("Target computer name configured.");
    }

    protected override async Task<DeploymentStepResult> ExecuteDryRunAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Request.UsesCustomUnattend)
        {
            return DeploymentStepResult.Skipped("Skipped because a custom answer file is selected.");
        }

        if (string.IsNullOrWhiteSpace(context.RuntimeState.TargetWindowsPartitionRoot))
        {
            return CreateMissingTargetPartitionFailure();
        }

        context.EmitCurrentStepIndeterminate("Configuring target computer name...", "Writing offline computer name...", DeploymentOperationNames.ConfigureComputerName);
        await _windowsDeploymentService
            .ConfigureOfflineComputerNameAsync(
                context.RuntimeState.TargetWindowsPartitionRoot,
                context.RuntimeState.TargetComputerName,
                context.Request.OperatingSystem.Architecture,
                context.Request.DefaultTimeZoneId,
                cancellationToken)
            .ConfigureAwait(false);

        await context.AppendLogAsync(
            DeploymentLogLevel.Info,
            "[DRY-RUN] Simulated target computer name configuration.",
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded("Target computer name configured (simulation).");
    }

    private static DeploymentStepResult CreateMissingTargetPartitionFailure() =>
        DeploymentStepResult.Failed(
            "Target Windows partition is unavailable.",
            DeploymentFailure.Guard(
                DeploymentOperationNames.ConfigureComputerName,
                DeploymentFailureReasons.MissingResource,
                "missing_target_partition"));
}
