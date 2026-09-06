// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Models;

public sealed record TargetDiskInfo
{
    public string UniqueId { get; init; } = string.Empty;
    /// <summary>Prevents synthetic wizard targets from becoming live erasure candidates.</summary>
    public bool IsSimulationOnly { get; init; }
    public int DiskNumber { get; init; }
    public string FriendlyName { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string BusType { get; init; } = string.Empty;
    public string PartitionStyle { get; init; } = string.Empty;
    public ulong SizeBytes { get; init; }
    public bool IsSystem { get; init; }
    public bool IsBoot { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsOffline { get; init; }
    public bool IsRemovable { get; init; }
    public bool IsSelectable { get; init; }
    public string SelectionWarning { get; init; } = string.Empty;

    public string DisplayLabel
    {
        get
        {
            string sizeGiB = SizeBytes > 0
                ? $"{(SizeBytes / 1024d / 1024d / 1024d):0.0} GiB"
                : Foundry.Deploy.Services.Localization.LocalizationText.GetString("Disk.UnknownSize");

            string warningSuffix = string.IsNullOrWhiteSpace(SelectionWarning)
                ? string.Empty
                : Foundry.Deploy.Services.Localization.LocalizationText.Format("Disk.WarningSuffixFormat", SelectionWarning);

            return Foundry.Deploy.Services.Localization.LocalizationText.Format(
                "Disk.DisplayLabelFormat",
                DiskNumber,
                FriendlyName,
                sizeGiB,
                BusType,
                warningSuffix);
        }
    }
}
