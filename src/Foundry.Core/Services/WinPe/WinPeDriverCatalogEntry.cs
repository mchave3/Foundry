// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

/// <summary>
/// Represents one downloadable WinPE driver package from the driver catalog.
/// </summary>
public sealed record WinPeDriverCatalogEntry
{
    /// <summary>
    /// Gets the stable catalog entry ID.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the display name of the driver package.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the package version.
    /// </summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// Gets the supported vendor selection.
    /// </summary>
    public WinPeVendorSelection Vendor { get; init; }

    /// <summary>
    /// Gets the package role in the driver preparation flow.
    /// </summary>
    public WinPeDriverPackageRole PackageRole { get; init; } = WinPeDriverPackageRole.BaseDriverPack;

    /// <summary>
    /// Gets the driver family used for filtering or display.
    /// </summary>
    public WinPeDriverFamily DriverFamily { get; init; } = WinPeDriverFamily.None;

    /// <summary>
    /// Gets the supported WinPE architecture.
    /// </summary>
    public WinPeArchitecture Architecture { get; init; }

    /// <summary>
    /// Gets the package download URI.
    /// </summary>
    public string DownloadUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the expected package file name in the local cache.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the archive or installer format used by the package.
    /// </summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>
    /// Gets the expected SHA-256 hash used for integrity validation.
    /// </summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>Identifies the freshly read catalog used to authorize signature-only acquisition.</summary>
    internal string CatalogRevision { get; init; } = string.Empty;

    /// <summary>
    /// Gets the vendor release date when the catalog provides one.
    /// </summary>
    public DateTimeOffset? ReleaseDate { get; init; }
}
