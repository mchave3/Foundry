// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Models.Configuration.Deploy;

/// <summary>
/// Defines packaged answer-file choices; a null default keeps native Foundry settings.
/// </summary>
public sealed record DeployUnattendSettings
{
    /// <summary>
    /// Gets whether custom answer-file selection is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets the selected default, or null for native settings.
    /// </summary>
    public string? DefaultFileId { get; init; }

    /// <summary>
    /// Gets the manifest of protected assets.
    /// </summary>
    public IReadOnlyList<DeployUnattendFile> Files { get; init; } = [];
}
