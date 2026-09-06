// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Utilities.Hardware;

/// <summary>Describes the firmware mode used to boot the running Windows environment.</summary>
public enum WindowsFirmwareType
{
    Unknown = 0,
    Bios = 1,
    Uefi = 2
}
