// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Models;

public sealed record HardwareProfile
{
    public Foundry.Utilities.Hardware.WindowsFirmwareType FirmwareType { get; init; }
    public string Manufacturer { get; init; } = "Unknown";
    public string Model { get; init; } = "Unknown";
    public string Product { get; init; } = "Unknown";
    public string SerialNumber { get; init; } = "Unknown";
    public string AssetTag { get; init; } = "Unknown";
    public string SystemUuid { get; init; } = "Unknown";
    public string Architecture { get; init; } = string.Empty;
    public bool IsVirtualMachine { get; init; }
    public bool IsOnBattery { get; init; }
    public bool IsTpmPresent { get; init; }
    public string SystemFirmwareHardwareId { get; init; } = string.Empty;
    public IReadOnlyList<PnpDeviceInfo> PnpDevices { get; init; } = Array.Empty<PnpDeviceInfo>();

    public string DisplayLabel => $"{Manufacturer} | {Model} | {Product} | {Architecture}";
}
