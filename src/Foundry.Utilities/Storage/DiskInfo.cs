// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Utilities.Storage;

/// <summary>
/// Describes raw Windows disk facts.
/// </summary>
public sealed record DiskInfo(
    int Number,
    string FriendlyName,
    string SerialNumber,
    string BusType,
    string PartitionStyle,
    ulong SizeBytes,
    bool IsSystem,
    bool IsBoot,
    bool IsReadOnly,
    bool IsOffline,
    bool IsRemovable)
{
    public string UniqueId { get; init; } = string.Empty;
}
