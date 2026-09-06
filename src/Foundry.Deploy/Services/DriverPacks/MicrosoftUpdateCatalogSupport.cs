// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text.RegularExpressions;
using Foundry.Deploy.Models;

namespace Foundry.Deploy.Services.DriverPacks;

internal static partial class MicrosoftUpdateCatalogSupport
{
    public static string[] BuildReleaseSearchOrder(string targetReleaseId)
    {
        var order = new List<string>();
        string normalizedTarget = string.IsNullOrWhiteSpace(targetReleaseId)
            ? string.Empty
            : targetReleaseId.Trim();

        if (OperatingSystemSupportMatrix.IsSupportedReleaseId(normalizedTarget))
        {
            order.Add(normalizedTarget);
        }

        foreach (string release in OperatingSystemSupportMatrix.ReleaseSearchOrder)
        {
            if (!order.Contains(release, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(release);
            }
        }

        return order.ToArray();
    }

    public static string? TryExtractDriverSearchHardwareId(PnpDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        string? match = TryExtractDriverSearchHardwareId(device.DeviceId);
        if (!string.IsNullOrWhiteSpace(match))
        {
            return match;
        }

        foreach (string hardwareId in device.HardwareIds)
        {
            match = TryExtractDriverSearchHardwareId(hardwareId);
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return null;
    }

    public static string? TryExtractDriverSearchHardwareId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = DriverHardwareIdRegex().Match(value);
        if (!match.Success)
        {
            match = SurfaceHardwareIdRegex().Match(value);
        }

        return match.Success ? match.Value : null;
    }

    public static string BuildSearchQuery(params string[] segments)
    {
        return string.Join(
            "+",
            segments
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .Select(segment => segment.Trim()));
    }

    public static string NormalizeArchitecture(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "amd64" => "x64",
            "x64" => "x64",
            "arm64" => "arm64",
            "aarch64" => "arm64",
            "x86" => "x86",
            _ => normalized
        };
    }

    public static MicrosoftUpdateCatalogDownload? SelectPreferredCab(IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads, string targetArchitecture)
    {
        if (downloads.Count == 0)
        {
            return null;
        }

        string normalizedArchitecture = NormalizeArchitecture(targetArchitecture);
        MicrosoftUpdateCatalogDownload[] cabDownloads = downloads
            .Where(download => IsCabUrl(download.DownloadUrl))
            .GroupBy(download => download.DownloadUrl, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (cabDownloads.Length == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(normalizedArchitecture))
        {
            return cabDownloads[0];
        }

        MicrosoftUpdateCatalogDownload? exactMatch = cabDownloads.FirstOrDefault(download => MatchesArchitecture(download, normalizedArchitecture));
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        MicrosoftUpdateCatalogDownload? compatibleFallback = cabDownloads.FirstOrDefault(download => !HasConflictingArchitecture(download, normalizedArchitecture));
        return compatibleFallback ?? cabDownloads[0];
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return "catalog";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return sanitized.Trim().TrimEnd('.');
    }

    private static bool IsCabUrl(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return Path.GetExtension(uri.AbsolutePath).Equals(".cab", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesArchitecture(MicrosoftUpdateCatalogDownload download, string targetArchitecture)
    {
        string fileName = ResolveComparableName(download);
        return targetArchitecture switch
        {
            "x64" => fileName.Contains("x64", StringComparison.Ordinal) || fileName.Contains("amd64", StringComparison.Ordinal),
            "arm64" => fileName.Contains("arm64", StringComparison.Ordinal),
            "x86" => fileName.Contains("x86", StringComparison.Ordinal) || fileName.Contains("32", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool HasConflictingArchitecture(MicrosoftUpdateCatalogDownload download, string targetArchitecture)
    {
        string fileName = ResolveComparableName(download);
        return targetArchitecture switch
        {
            "x64" => fileName.Contains("arm64", StringComparison.Ordinal) || fileName.Contains("x86", StringComparison.Ordinal),
            "arm64" => fileName.Contains("x64", StringComparison.Ordinal) ||
                       fileName.Contains("amd64", StringComparison.Ordinal) ||
                       fileName.Contains("x86", StringComparison.Ordinal),
            "x86" => fileName.Contains("arm64", StringComparison.Ordinal) ||
                     fileName.Contains("x64", StringComparison.Ordinal) ||
                     fileName.Contains("amd64", StringComparison.Ordinal),
            _ => false
        };
    }

    private static string ResolveComparableName(MicrosoftUpdateCatalogDownload download)
    {
        string fileName = string.IsNullOrWhiteSpace(download.FileName)
            ? ResolveFileNameFromUrl(download.DownloadUrl)
            : download.FileName;

        return $"{fileName} {download.Architectures}".ToLowerInvariant();
    }

    [GeneratedRegex("v[ei][dn]_([0-9a-f]){4}&[pd][ie][dv]_([0-9a-f]){4}", RegexOptions.IgnoreCase)]
    private static partial Regex DriverHardwareIdRegex();

    [GeneratedRegex("mshw0[0-1]([0-9]){2}", RegexOptions.IgnoreCase)]
    private static partial Regex SurfaceHardwareIdRegex();
}
