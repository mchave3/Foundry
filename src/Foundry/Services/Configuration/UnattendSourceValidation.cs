// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.Configuration;

namespace Foundry.Services.Configuration;

/// <summary>
/// Caches source inspection metadata and safe failures without retaining sensitive XML bytes.
/// </summary>
public sealed record UnattendSourceValidation(
    UnattendFileSettings File,
    UnattendInspection? Inspection,
    string? ErrorMessage);
