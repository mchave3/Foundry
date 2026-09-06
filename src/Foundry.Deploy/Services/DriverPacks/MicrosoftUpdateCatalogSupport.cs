// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text.RegularExpressions;
using Foundry.Deploy.Models;

namespace Foundry.Deploy.Services.DriverPacks;

internal static partial class MicrosoftUpdateCatalogSupport
{
    public static string[] BuildReleaseSearchOrder(string targetReleaseId) =>
        OperatingSystemSupportMatrix.IsSupportedReleaseId(targetReleaseId.Trim()) ? [targetReleaseId.Trim()] : [];

    public static string? TryExtractDriverSearchHardwareId(PnpDeviceInfo device) => BuildHardwareSearchOrder(device).FirstOrDefault();

    public static string? TryExtractDriverSearchHardwareId(string value)
    {
        string[] parts = value.Trim().Split('\\');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return null;
        }
        string hardwareId = $"{parts[0]}\\{parts[1]}";
        return hardwareId.Length <= 512 && hardwareId.All(ch => char.IsAsciiLetterOrDigit(ch) || "\\_&{}.-".Contains(ch))
            ? hardwareId : null;
    }

    internal static string[] BuildHardwareSearchOrder(PnpDeviceInfo device) => device.HardwareIds.Prepend(device.DeviceId)
        .Select(TryExtractDriverSearchHardwareId)
        .OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(id => id.Contains("&SUBSYS_", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(id => id.Length)
        .ToArray();

    public static string BuildSearchQuery(params string[] segments) =>
        string.Join("+", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)).Select(segment => segment.Trim()));

    public static string NormalizeArchitecture(string value) => value.Trim().ToLowerInvariant() switch
    {
        "amd64" or "x64" => "x64",
        "aarch64" or "arm64" => "arm64",
        "x86" => "x86",
        var normalized => normalized
    };

    public static MicrosoftUpdateCatalogDownload? SelectPreferredCab(IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads, string targetArchitecture) =>
        GetCabCandidates(downloads, targetArchitecture).FirstOrDefault(download => HasExplicitArchitecture(download, targetArchitecture));

    internal static MicrosoftUpdateCatalogDownload[] GetCabCandidates(IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads, string targetArchitecture)
    {
        string target = NormalizeArchitecture(targetArchitecture);
        if (target is not ("x64" or "arm64" or "x86"))
        {
            return [];
        }
        return downloads.Where(download => IsCabUrl(download.DownloadUrl) &&
                Path.GetExtension(download.FileName).Equals(".cab", StringComparison.OrdinalIgnoreCase) &&
                !HasConflictingArchitecture(download, target))
            .Distinct()
            .ToArray();
    }

    internal static bool AllowsTargetRelease(MicrosoftUpdateCatalogUpdate update, OperatingSystemCatalogItem target)
    {
        if (target.BuildMajor <= 0 || !OperatingSystemSupportMatrix.IsSupported(target))
        {
            return false;
        }
        string metadata = $"{update.Products} {update.Title}";
        MatchCollection windowsVersions = WindowsVersionRegex().Matches(metadata);
        if (windowsVersions.Count > 0 && !windowsVersions.Any(match => match.Groups[1].Value == target.WindowsRelease.Trim()))
        {
            return false;
        }
        MatchCollection releases = ReleaseRegex().Matches(metadata);
        return releases.Count == 0 || releases.Any(match =>
            match.Groups[1].Value.Equals(target.ReleaseId, StringComparison.OrdinalIgnoreCase) ||
            (match.Groups[2].Success && string.Compare(match.Groups[1].Value, target.ReleaseId, StringComparison.OrdinalIgnoreCase) < 0));
    }

    /// <summary>Keeps fallback cache bytes on the staging volume but outside its recursively consumed raw tree.</summary>
    internal static string ResolveFallbackCacheRoot(string stagingRoot)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        string parent = Path.GetDirectoryName(fullRoot)
            ?? throw new InvalidOperationException("A staging directory with a parent is required for fallback cache placement.");
        return Path.Combine(parent, $"{Path.GetFileName(fullRoot)}.cache");
    }

    public static string ResolveFileNameFromUrl(string downloadUrl)
    {
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri))
        {
            string fileName = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return SanitizePathSegment(fileName);
            }
        }
        return $"catalog-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.cab";
    }

    public static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "catalog";
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return sanitized.Trim().TrimEnd('.');
    }

    private static bool IsCabUrl(string downloadUrl) => Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) &&
        Path.GetExtension(uri.AbsolutePath).Equals(".cab", StringComparison.OrdinalIgnoreCase);

    private static bool HasExplicitArchitecture(MicrosoftUpdateCatalogDownload download, string target) =>
        ArchitectureTokens(download).Contains(NormalizeArchitecture(target), StringComparer.Ordinal);

    private static bool HasConflictingArchitecture(MicrosoftUpdateCatalogDownload download, string target)
    {
        string[] filenameArchitectures = ArchitectureTokens(download.FileName);
        string[] catalogArchitectures = ArchitectureTokens(download.Architectures);
        return filenameArchitectures.Length > 0 && !filenameArchitectures.Contains(target, StringComparer.Ordinal) ||
            catalogArchitectures.Length > 0 && !catalogArchitectures.Contains(target, StringComparer.Ordinal);
    }

    private static string[] ArchitectureTokens(MicrosoftUpdateCatalogDownload download) =>
        ArchitectureTokens($"{download.FileName} {download.Architectures}");

    private static string[] ArchitectureTokens(string value) => ArchitectureRegex().Matches(value)
            .Select(match => NormalizeArchitecture(match.Value)).Distinct(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"(?<![a-z0-9])(amd64|x64|arm64|aarch64|x86)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureRegex();

    [GeneratedRegex(@"\b(\d{2}H[12])\b(\s+and\s+later)?", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseRegex();

    [GeneratedRegex(@"\bWindows\s+(10|11)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsVersionRegex();
}
