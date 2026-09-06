// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;

namespace Foundry.Utilities.Hardware;

/// <summary>Reads actual boot firmware; unavailable native evidence remains unknown.</summary>
public static class WindowsFirmwareInspector
{
    public static WindowsFirmwareType GetCurrent() => OperatingSystem.IsWindows()
        ? Read(GetFirmwareType) : WindowsFirmwareType.Unknown;

    internal delegate bool FirmwareReader(out uint firmwareType);

    internal static WindowsFirmwareType Read(FirmwareReader read) =>
        read(out uint value) && value is 1 or 2 ? (WindowsFirmwareType)value : WindowsFirmwareType.Unknown;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);
}
