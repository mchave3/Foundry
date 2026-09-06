// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>Binds a newly created GPT partition to its post-format volume root and geometry.</summary>
public sealed record DeploymentPartitionIdentity(Guid PartitionId, ulong Offset, ulong Size, string VolumeRoot, char DriveLetter)
{
    public void Validate()
    {
        if (PartitionId == Guid.Empty || Offset == 0 || Size == 0 ||
            !Regex.IsMatch(VolumeRoot, @"^\\\\\?\\Volume\{[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}\\$"))
        {
            throw new InvalidOperationException("The prepared partition identity is incomplete.");
        }
    }
}
