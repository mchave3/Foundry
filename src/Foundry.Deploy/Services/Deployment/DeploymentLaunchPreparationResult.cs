// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;

namespace Foundry.Deploy.Services.Deployment;

public sealed record DeploymentLaunchPreparationResult
{
    public string? FailureMessage { get; init; }
    public required bool IsReadyToStart { get; init; }
    public required string NormalizedComputerName { get; init; }
    public TargetDiskInfo? EffectiveTargetDisk { get; init; }
    public DeploymentContext? Context { get; init; }

    public static DeploymentLaunchPreparationResult Failure(string normalizedComputerName, string? failureMessage = null)
    {
        return new DeploymentLaunchPreparationResult
        {
            FailureMessage = failureMessage,
            IsReadyToStart = false,
            NormalizedComputerName = normalizedComputerName
        };
    }

    public static DeploymentLaunchPreparationResult Success(
        string normalizedComputerName,
        TargetDiskInfo effectiveTargetDisk,
        DeploymentContext context)
    {
        return new DeploymentLaunchPreparationResult
        {
            IsReadyToStart = true,
            NormalizedComputerName = normalizedComputerName,
            EffectiveTargetDisk = effectiveTargetDisk,
            Context = context
        };
    }
}
