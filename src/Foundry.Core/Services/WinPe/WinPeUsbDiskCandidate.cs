// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

public sealed record WinPeUsbDiskCandidate
{
    public int DiskNumber { get; init; }
    public string FriendlyName { get; init; } = string.Empty;
    public string DriveLetters { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string UniqueId { get; init; } = string.Empty;
    public string BusType { get; init; } = string.Empty;
    public bool? IsRemovable { get; init; }
    public bool IsSystem { get; init; }
    public bool IsBoot { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public ulong SizeBytes { get; init; }
    public bool IsFoundryMedia { get; init; }
}
