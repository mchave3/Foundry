// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Utilities.Hardware;

/// <summary>
/// Describes hardware facts for the current machine.
/// </summary>
public sealed record HardwareSnapshot(
    string Manufacturer,
    string Model,
    string Product,
    string SerialNumber,
    string Architecture,
    bool IsVirtualMachine,
    bool IsOnBattery,
    bool IsTpmPresent,
    string SystemFirmwareHardwareId,
    IReadOnlyList<PnpDeviceSnapshot> PnpDevices)
{
    public WindowsFirmwareType FirmwareType { get; init; }

    public string AssetTag { get; init; } = string.Empty;

    public string SystemUuid { get; init; } = string.Empty;
}
