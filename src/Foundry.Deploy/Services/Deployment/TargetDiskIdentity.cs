// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>Retains the device facts shown at destructive confirmation; never refreshes the expected identity.</summary>
public sealed record TargetDiskIdentity(int DiskNumber, string UniqueId, string SerialNumber, ulong SizeBytes, string BusType)
{
    /// <summary>Copies identity facts from the immutable disk selected at confirmation.</summary>
    public static TargetDiskIdentity FromDisk(TargetDiskInfo disk) =>
        new(disk.DiskNumber, disk.UniqueId.Trim(), disk.SerialNumber.Trim(), disk.SizeBytes, disk.BusType.Trim());

    /// <summary>Requires one exact stable identity and unchanged location, geometry, bus and safety facts.</summary>
    public TargetDiskInfo? Match(IReadOnlyList<TargetDiskInfo> disks)
    {
        if (DiskNumber < 0 || SizeBytes == 0 || string.IsNullOrWhiteSpace(BusType)) return null;
        string uniqueId = UniqueId.Trim();
        string serial = SerialNumber.Trim();
        if (uniqueId.Length == 0 && serial.Length == 0) return null;
        TargetDiskInfo[] matches = disks.Where(disk => uniqueId.Length > 0
            ? disk.UniqueId.Trim().Equals(uniqueId, StringComparison.Ordinal)
            : disk.SerialNumber.Trim().Equals(serial, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) return null;
        TargetDiskInfo current = matches[0];
        return current.DiskNumber == DiskNumber && current.SizeBytes == SizeBytes &&
            current.BusType.Trim().Equals(BusType.Trim(), StringComparison.Ordinal) &&
            current.SerialNumber.Trim().Equals(serial, StringComparison.Ordinal) &&
            current.IsSelectable && !current.IsSimulationOnly && !current.IsBoot && !current.IsSystem && !current.IsReadOnly && !current.IsOffline
            ? current : null;
    }

    public override string ToString() => $"Confirmed target disk {DiskNumber}";
}
