// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Services.Deployment.Unattend;
using Foundry.Deploy.Services.Logging;

namespace Foundry.Deploy.Services.Deployment.Steps;

/// <summary>Validates and retains custom answer-file bytes before any destructive disk operation.</summary>
public sealed class ValidateCustomUnattendStep(UnattendContentService contentService) : DeploymentStepBase
{
    public override string Name => DeploymentStepNames.ValidateCustomUnattend;

    protected override async Task<DeploymentStepResult> ExecuteLiveAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Request.Unattend is null)
        {
            return DeploymentStepResult.Skipped("Native Foundry settings selected.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        context.UnattendSnapshot?.Dispose();
        context.UnattendSnapshot = contentService.Read(context.Request.Unattend, context.Request.OperatingSystem.Architecture,
            context.Request.IsAutopilotEnabled, context.Request.AutopilotProvisioningMode);
        if (context.UnattendSnapshot.Inspection.HasCommands)
        {
            await context.AppendLogAsync(DeploymentLogLevel.Warning,
                "Custom commands may overlap with Foundry setup hooks, network roaming, customization and enrollment. Compatibility requires a deployment test.", cancellationToken).ConfigureAwait(false);
        }
        return DeploymentStepResult.Succeeded("Custom answer file validated before disk preparation.");
    }

    protected override Task<DeploymentStepResult> ExecuteDryRunAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken) =>
        ExecuteLiveAsync(context, cancellationToken);
}
