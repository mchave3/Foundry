// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Models.Configuration.Deploy;

/// <summary>
/// Describes a protected answer-file asset without source paths or XML content.
/// </summary>
public sealed record DeployUnattendFile
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
    /// Gets the SHA-256 digest of the original decrypted bytes.
    /// </summary>
    public string ContentHash { get; init; } = string.Empty;
}
