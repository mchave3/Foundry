// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Models;

/// <summary>Describes whether catalog metadata establishes a system pack or an accessory package.</summary>
public enum DriverPackPackageRole
{
    /// <summary>Requires a documented system mapping before automatic selection.</summary>
    Unknown,
    /// <summary>A system driver pack still requiring an exact hardware and OS match.</summary>
    System,
    /// <summary>A peripheral package that cannot be an automatic system driver pack.</summary>
    Accessory
}
