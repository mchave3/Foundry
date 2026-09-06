// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Services.Deployment;

public sealed record DeploymentTargetLayout
{
    public TargetDiskIdentity? DiskIdentity { get; init; }
    public DeploymentPartitionIdentity? SystemPartition { get; init; }
    public DeploymentPartitionIdentity? WindowsPartition { get; init; }
    public DeploymentPartitionIdentity? RecoveryPartition { get; init; }
    public required int DiskNumber { get; init; }
    public required string SystemPartitionRoot { get; init; }
    public required string WindowsPartitionRoot { get; init; }
    public required string RecoveryPartitionRoot { get; init; }
    public required char RecoveryPartitionLetter { get; init; }
}
