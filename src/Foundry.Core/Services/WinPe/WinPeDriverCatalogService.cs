// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Foundry.Utilities.Networking;

namespace Foundry.Core.Services.WinPe;

/// <summary>
/// Loads WinPE driver catalog XML from HTTP or disk and maps matching entries to catalog records.
/// </summary>
public sealed class WinPeDriverCatalogService : IWinPeDriverCatalogService
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a driver catalog service with a default HTTP client.
    /// </summary>
    public WinPeDriverCatalogService()
        : this(new HttpClient(new ValidatedRedirectHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
            static uri =>
            {
                if (uri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidDataException("Driver catalog requests require HTTPS.");
                }
            })) { Timeout = TimeSpan.FromSeconds(30) })
    {
    }

    internal WinPeDriverCatalogService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>>> GetCatalogAsync(
        WinPeDriverCatalogOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WinPeDiagnostic? validationError = ValidateCatalogOptions(options);
        if (validationError is not null)
        {
            return WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>>.Failure(validationError);
        }

        string xmlContent;
        try
        {
            if (Uri.TryCreate(options.CatalogUri, UriKind.Absolute, out Uri? uri) &&
                (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                if (uri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidDataException("Driver catalog requests require HTTPS.");
                }

                xmlContent = await HttpRetry.ExecuteAsync(async token =>
                {
                    using HttpResponseMessage response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
                    {
                        throw new InvalidDataException("Driver catalog redirects require HTTPS.");
                    }
                    return await BoundedHttpContent.ReadStringAsync(response, 32 * 1024 * 1024, token).ConfigureAwait(false);
                }, new HttpRetryOptions(3, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Local catalog paths are supported to allow offline media creation and catalog testing.
                await using FileStream catalog = new(options.CatalogUri, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (catalog.Length > 32 * 1024 * 1024)
                {
                    throw new InvalidDataException("The local driver catalog exceeds the permitted size.");
                }
                using var reader = new StreamReader(catalog, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                xmlContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>>.Failure(
                WinPeErrorCodes.DriverCatalogFetchFailed,
                "Failed to retrieve the WinPE driver catalog.",
                failureKind: WinPeDriverPackageService.CreateAcquisitionFailure(ex).FailureKind,
                failureReason: WinPeDriverPackageService.CreateAcquisitionFailure(ex).FailureReason,
                errorSummary: ex.Message,
                exception: ex);
        }

        try
        {
            XDocument document = XDocument.Parse(xmlContent);
            string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xmlContent)));
            IReadOnlyList<WinPeDriverCatalogEntry> entries = ParseDriverPacks(document, options)
                .Select(entry => entry with { CatalogRevision = revision }).ToArray();
            return WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>>.Success(entries);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Xml.XmlException)
        {
            return WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>>.Failure(
                WinPeErrorCodes.DriverCatalogParseFailed,
                "Failed to parse the WinPE driver catalog.",
                ex.Message);
        }
    }

    private static IReadOnlyList<WinPeDriverCatalogEntry> ParseDriverPacks(
        XDocument document,
        WinPeDriverCatalogOptions options)
    {
        var entries = new List<WinPeDriverCatalogEntry>();
        HashSet<WinPeVendorSelection> requestedVendors = options.Vendors
            .Where(vendor => vendor != WinPeVendorSelection.Any)
            .ToHashSet();

        foreach (XElement pack in document.Descendants("DriverPack"))
        {
            string downloadUrl = (string?)pack.Attribute("downloadUrl") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            XElement? osInfo = pack.Element("OsInfo");
            WinPeVendorSelection vendor = ParseVendor((string?)pack.Attribute("manufacturer"));
            if (requestedVendors.Count > 0 && !requestedVendors.Contains(vendor))
            {
                continue;
            }

            string name = (string?)pack.Attribute("name") ?? string.Empty;
            string releaseId = (string?)osInfo?.Attribute("releaseId") ?? string.Empty;
            if (!MatchesRequiredWinPeRelease(releaseId, options.RequiredWinPeReleaseId, name))
            {
                continue;
            }

            WinPeArchitecture? architecture = ParseArchitecture((string?)osInfo?.Attribute("architecture"));
            if (architecture is null || architecture.Value != options.Architecture)
            {
                continue;
            }

            string id = (string?)pack.Attribute("id") ?? string.Empty;
            string version = (string?)pack.Attribute("version") ?? string.Empty;
            if (name.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
                version.Contains("preview", StringComparison.OrdinalIgnoreCase))
            {
                // Preview packages are intentionally excluded from boot media because recovery environments need stable drivers.
                continue;
            }

            if (!MatchesSearchTerm(options.SearchTerm, name, id, version))
            {
                continue;
            }

            entries.Add(new WinPeDriverCatalogEntry
            {
                Id = id,
                Name = name,
                Version = version,
                Vendor = vendor,
                PackageRole = ParsePackageRole((string?)pack.Attribute("packageRole")),
                DriverFamily = ParseDriverFamily((string?)pack.Attribute("driverFamily")),
                Architecture = architecture.Value,
                DownloadUri = downloadUrl,
                FileName = (string?)pack.Attribute("fileName") ?? string.Empty,
                Format = (string?)pack.Attribute("format") ?? string.Empty,
                Sha256 = (string?)pack.Element("Hashes")?.Attribute("sha256") ?? string.Empty,
                ReleaseDate = TryParseDate((string?)pack.Attribute("releaseDate"))
            });
        }

        return entries
            .OrderByDescending(entry => entry.ReleaseDate ?? DateTimeOffset.MinValue)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesSearchTerm(string? searchTerm, string name, string id, string version)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return true;
        }

        string term = searchTerm.Trim();
        return name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               version.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static WinPeVendorSelection ParseVendor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return WinPeVendorSelection.Any;
        }

        string normalized = value.Trim();
        if (normalized.Equals("dell", StringComparison.OrdinalIgnoreCase))
        {
            return WinPeVendorSelection.Dell;
        }

        if (normalized.Equals("hp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("hewlett-packard", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("hewlett packard", StringComparison.OrdinalIgnoreCase))
        {
            return WinPeVendorSelection.Hp;
        }

        if (normalized.Equals("lenovo", StringComparison.OrdinalIgnoreCase))
        {
            return WinPeVendorSelection.Lenovo;
        }

        if (normalized.Equals("microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return WinPeVendorSelection.Microsoft;
        }

        if (normalized.Equals("intel", StringComparison.OrdinalIgnoreCase))
        {
            return WinPeVendorSelection.Intel;
        }

        return WinPeVendorSelection.Any;
    }

    private static WinPeDriverPackageRole ParsePackageRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return WinPeDriverPackageRole.BaseDriverPack;
        }

        return value.Trim() switch
        {
            "WifiSupplement" => WinPeDriverPackageRole.WifiSupplement,
            _ => WinPeDriverPackageRole.BaseDriverPack
        };
    }

    private static WinPeDriverFamily ParseDriverFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return WinPeDriverFamily.None;
        }

        return value.Trim() switch
        {
            "IntelWireless" => WinPeDriverFamily.IntelWireless,
            _ => WinPeDriverFamily.None
        };
    }

    private static WinPeArchitecture? ParseArchitecture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "x64" or "amd64" => WinPeArchitecture.X64,
            "arm64" or "aarch64" => WinPeArchitecture.Arm64,
            _ => null
        };
    }

    private static DateTimeOffset? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static bool MatchesRequiredWinPeRelease(string releaseId, string? requiredReleaseId, string name)
    {
        if (string.IsNullOrWhiteSpace(requiredReleaseId))
        {
            return true;
        }

        string normalizedRequired = requiredReleaseId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRequired))
        {
            return true;
        }

        string[] releaseTokens = SplitReleaseTokens(releaseId);
        if (releaseTokens.Any(token => token.Equals(normalizedRequired, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return name.Contains($"WinPE{normalizedRequired}", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SplitReleaseTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(['/', '\\', ',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
    }

    private static WinPeDiagnostic? ValidateCatalogOptions(WinPeDriverCatalogOptions? options)
    {
        if (options is null)
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Driver catalog options are required.",
                "Provide a non-null WinPeDriverCatalogOptions instance.");
        }

        if (string.IsNullOrWhiteSpace(options.CatalogUri))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Driver catalog URI is required.",
                "Set WinPeDriverCatalogOptions.CatalogUri.");
        }

        if (!Enum.IsDefined(options.Architecture))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Architecture value is invalid.",
                $"Value: '{options.Architecture}'.");
        }

        foreach (WinPeVendorSelection vendor in options.Vendors)
        {
            if (!Enum.IsDefined(vendor))
            {
                return new WinPeDiagnostic(
                    WinPeErrorCodes.ValidationFailed,
                    "Vendor selection value is invalid.",
                    $"Value: '{vendor}'.");
            }
        }

        return null;
    }
}
