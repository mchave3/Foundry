// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Services.DriverPacks;

public sealed record MicrosoftUpdateCatalogDownload
{
    /// <summary>Identifies the authenticated bounded catalog response that supplied this metadata.</summary>
    public string CatalogRevision { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string Sha1 { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string Architectures { get; init; } = string.Empty;

    public string Languages { get; init; } = string.Empty;
}
