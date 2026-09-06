// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Models;

public static class TargetDiskInfoFactory
{
    public static TargetDiskInfo CreateDebugVirtualDisk()
    {
        return new TargetDiskInfo
        {
            IsSimulationOnly = true,
            DiskNumber = 999,
            FriendlyName = Foundry.Deploy.Services.Localization.LocalizationText.GetString("Disk.DebugVirtualTarget"),
            SerialNumber = "DEBUG-ONLY",
            BusType = "Virtual",
            PartitionStyle = "GPT",
            SizeBytes = 128UL * 1024UL * 1024UL * 1024UL,
            IsSystem = false,
            IsBoot = false,
            IsReadOnly = false,
            IsOffline = false,
            IsRemovable = false,
            IsSelectable = true,
            SelectionWarning = string.Empty
        };
    }
}
