// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Models.Configuration;

/// <summary>
/// Defines the authoring catalog of referenced answer files; a null default keeps native Foundry settings.
/// </summary>
public sealed record UnattendSettings
{
    /// <summary>
    /// Gets whether custom answer files are packaged.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets the selected default, or null for native settings.
    /// </summary>
    public string? DefaultFileId { get; init; }

    /// <summary>
    /// Gets retained source references, including while the feature is disabled.
    /// </summary>
    public IReadOnlyList<UnattendFileSettings> Files { get; init; } = [];
}
