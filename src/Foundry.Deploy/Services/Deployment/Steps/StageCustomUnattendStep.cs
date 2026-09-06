// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;

namespace Foundry.Deploy.Services.Deployment.Steps;

/// <summary>Stages the exact prevalidated custom answer file after image application.</summary>
public sealed class StageCustomUnattendStep : DeploymentStepBase
{
    public override string Name => DeploymentStepNames.StageCustomUnattend;

    protected override async Task<DeploymentStepResult> ExecuteLiveAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.UsesCustomUnattend)
        {
            return DeploymentStepResult.Skipped("Native Foundry settings selected.");
        }
        if (context.UnattendSnapshot is null || string.IsNullOrWhiteSpace(context.RuntimeState.TargetWindowsPartitionRoot))
        {
            throw new InvalidOperationException("A validated answer file and target Windows partition are required.");
        }
        try
        {
            await context.UnattendSnapshot.StageAsync(context.RuntimeState.TargetWindowsPartitionRoot, cancellationToken).ConfigureAwait(false);
            return DeploymentStepResult.Succeeded("Custom answer file staged unchanged. Foundry does not merge or add settings.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new IOException("The custom answer file could not be staged to the target Windows installation.");
        }
        finally
        {
            context.UnattendSnapshot.Dispose();
            context.UnattendSnapshot = null;
        }
    }

    protected override Task<DeploymentStepResult> ExecuteDryRunAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Request.UsesCustomUnattend)
        {
            return Task.FromResult(DeploymentStepResult.Skipped("Native Foundry settings selected."));
        }
        if (context.UnattendSnapshot is null)
        {
            throw new InvalidOperationException("A validated answer file is required.");
        }
        context.UnattendSnapshot.Dispose();
        context.UnattendSnapshot = null;
        return Task.FromResult(DeploymentStepResult.Succeeded("Custom answer-file staging simulated; no plaintext file was written."));
    }
}
