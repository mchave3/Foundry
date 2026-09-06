// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Models;

public sealed record DriverPackCatalogItem
{
    /// <summary>Identifies the authenticated bounded catalog response that supplied this metadata.</summary>
    public string CatalogRevision { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string PackageId { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Format { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    /// <summary>Contains the role established from documented catalog metadata, never inferred from architecture.</summary>
    public DriverPackPackageRole PackageRole { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public string OsName { get; init; } = string.Empty;
    public string OsReleaseId { get; init; } = string.Empty;
    public string OsArchitecture { get; init; } = string.Empty;
    public IReadOnlyList<string> ModelNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SystemIds { get; init; } = Array.Empty<string>();
    public string Sha256 { get; init; } = string.Empty;

    public string DisplayLabel =>
        $"{Manufacturer} | {Name} | {OsName} {OsArchitecture}";
}
