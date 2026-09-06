// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Models.Configuration;

/// <summary>
/// Identifies a validated answer-file source without persisting its potentially sensitive XML.
/// </summary>
public sealed record UnattendFileSettings
{
    /// <summary>
    /// Gets the generated stable identifier used for the protected asset filename.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the operator-facing label.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the authoring-only source reference required until media is built.
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the SHA-256 digest used to detect changes requiring explicit refresh.
    /// </summary>
    public string ContentHash { get; init; } = string.Empty;
}
