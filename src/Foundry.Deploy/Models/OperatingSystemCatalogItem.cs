// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Models;

public sealed record OperatingSystemCatalogItem
{
    /// <summary>Identifies the authenticated bounded catalog response that supplied this metadata.</summary>
    public string CatalogRevision { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string ClientType { get; init; } = string.Empty;
    public string WindowsRelease { get; init; } = string.Empty;
    public string ReleaseId { get; init; } = string.Empty;
    public string Build { get; init; } = string.Empty;
    public int BuildMajor { get; init; }
    public int BuildUbr { get; init; }
    public DateOnly MediaDate { get; init; }
    public string Architecture { get; init; } = string.Empty;
    public string LanguageCode { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Edition { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string LicenseChannel { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Sha1 { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;

    public string DisplayLabel =>
        $"{Foundry.Deploy.Converters.OperatingSystemDisplayFormatter.FormatWindowsRelease(WindowsRelease)} {ReleaseId} | {Architecture} | {LanguageCode} | {Edition.Trim()} | {Foundry.Deploy.Converters.OperatingSystemDisplayFormatter.FormatLicenseChannel(LicenseChannel)} | {Build}";
}
