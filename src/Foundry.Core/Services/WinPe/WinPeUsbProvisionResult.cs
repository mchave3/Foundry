// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

public sealed record WinPeUsbProvisionResult
{
    /// <summary>Confirmed disk and retained partition/volume identities used to revalidate population.</summary>
    public WinPeUsbDiskIdentity? ConfirmedDisk { get; init; }
    public int BootPartitionNumber { get; init; }
    public int CachePartitionNumber { get; init; }
    public ulong BootPartitionOffset { get; init; }
    public ulong CachePartitionOffset { get; init; }
    public ulong BootPartitionSize { get; init; }
    public ulong CachePartitionSize { get; init; }
    public string BootPartitionGuid { get; init; } = string.Empty;
    public string CachePartitionGuid { get; init; } = string.Empty;
    public string BootVolumeUniqueId { get; init; } = string.Empty;
    public string CacheVolumeUniqueId { get; init; } = string.Empty;
    public string BootVolumePath { get; init; } = string.Empty;
    public string CacheVolumePath { get; init; } = string.Empty;
    public string BootDriveLetter { get; init; } = string.Empty;
    public string CacheDriveLetter { get; init; } = string.Empty;
}
