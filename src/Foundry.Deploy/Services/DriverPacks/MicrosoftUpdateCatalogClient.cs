// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.IO;
using System.Text.Json;
using Foundry.Deploy.Services.Catalog;
using Foundry.Deploy.Services.Download;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Foundry.Deploy.Services.Http;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.DriverPacks;

public sealed class MicrosoftUpdateCatalogClient : IMicrosoftUpdateCatalogClient
{
    private static readonly HttpClient HttpClient = AcquisitionHttpClientFactory.Create(TimeSpan.FromSeconds(20));
    private static readonly Uri HomeUri = new("https://www.catalog.update.microsoft.com/Home.aspx");
    private static readonly Uri DownloadDialogUri = new("https://www.catalog.update.microsoft.com/DownloadDialog.aspx");
    private static readonly Regex DownloadPropertyRegex = new(
        "downloadInformation\\[\\d+\\]\\.files\\[(?<index>\\d+)\\]\\.(?<name>[A-Za-z0-9_]+)\\s*=\\s*'(?<value>(?:\\\\'|[^'])*)'",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<MicrosoftUpdateCatalogClient> _logger;

    public MicrosoftUpdateCatalogClient(ILogger<MicrosoftUpdateCatalogClient> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SendStringAsync(HomeUri.AbsoluteUri, "Microsoft Update Catalog availability", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Microsoft Update Catalog is unavailable. FailureType={FailureType}", ex.GetType().Name);
            return false;
        }
    }

    public async Task<IReadOnlyList<MicrosoftUpdateCatalogUpdate>> SearchAsync(
        string searchQuery,
        bool descending = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchQuery);

        string encodedQuery = Uri.EscapeDataString(searchQuery);
        string requestUri = $"https://www.catalog.update.microsoft.com/Search.aspx?q={encodedQuery}";
        string html = await SendStringAsync(requestUri, "Microsoft Update Catalog search", cancellationToken).ConfigureAwait(false);

        var document = new HtmlDocument();
        document.LoadHtml(html);

        HtmlNode? errorNode = document.GetElementbyId("errorPageDisplayedError");
        if (errorNode is not null)
        {
            _logger.LogWarning("Microsoft Update Catalog search returned an error page.");
            return [];
        }

        if (document.GetElementbyId("ctl00_catalogBody_noResultText") is not null)
        {
            return [];
        }

        HtmlNode? table = document.GetElementbyId("ctl00_catalogBody_updateMatches");
        if (table is null)
        {
            _logger.LogWarning("Microsoft Update Catalog search did not return the expected results table for query '{SearchQuery}'.", searchQuery);
            return [];
        }

        HtmlNodeCollection? rows = table.SelectNodes(".//tr");
        if (rows is null || rows.Count == 0)
        {
            return [];
        }

        IEnumerable<MicrosoftUpdateCatalogUpdate> parsed = rows
            .Where(row => !string.Equals(row.Id, "headerRow", StringComparison.OrdinalIgnoreCase))
            .Select(ParseUpdate)
            .Where(update => update is not null)
            .Cast<MicrosoftUpdateCatalogUpdate>();

        return descending
            ? parsed.OrderByDescending(update => update.LastUpdated ?? DateTimeOffset.MinValue).ThenBy(update => update.Title, StringComparer.OrdinalIgnoreCase).ToArray()
            : parsed.ToArray();
    }

    public async Task<IReadOnlyList<MicrosoftUpdateCatalogDownload>> GetDownloadsAsync(
        string updateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updateId);

        string payload = JsonSerializer.Serialize(new[] { new { size = 0, updateID = updateId, uidInfo = updateId } });
        string html = await SendFormAsync(
                DownloadDialogUri.ToString(),
                [new KeyValuePair<string, string>("updateIDs", payload)],
                "Microsoft Update Catalog download dialog",
                cancellationToken)
            .ConfigureAwait(false);

        return ParseDownloads(html, _logger);
    }

    internal static IReadOnlyList<MicrosoftUpdateCatalogDownload> ParseDownloads(string html, ILogger<MicrosoftUpdateCatalogClient> logger)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        string revision = CatalogContentIdentity.Calculate(html);
        Dictionary<int, Dictionary<string, string>> files = [];
        foreach (Match match in DownloadPropertyRegex.Matches(html))
        {
            if (!int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                continue;
            }

            if (!files.TryGetValue(index, out Dictionary<string, string>? properties))
            {
                properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                files[index] = properties;
            }

            properties[match.Groups["name"].Value] = UnescapeJavascriptString(match.Groups["value"].Value);
        }

        return files
            .OrderBy(pair => pair.Key)
            .Select(pair => CreateDownload(pair.Value, logger))
            .Where(download => download is not null)
            .Cast<MicrosoftUpdateCatalogDownload>()
            .Select(download => download with { CatalogRevision = revision })
            .ToArray();
    }

    private static MicrosoftUpdateCatalogDownload? CreateDownload(Dictionary<string, string> properties, ILogger<MicrosoftUpdateCatalogClient> logger)
    {
        string downloadUrl = properties.GetValueOrDefault("url") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        var sourceUri = new Uri(downloadUrl, UriKind.Absolute);
        if (sourceUri.Host.Equals("www.download.windowsupdate.com", StringComparison.OrdinalIgnoreCase))
        {
            sourceUri = new UriBuilder(sourceUri) { Host = "download.windowsupdate.com" }.Uri;
        }
        string fileName = properties.GetValueOrDefault("fileName") ?? Path.GetFileName(sourceUri.LocalPath);
        ArtifactIntegrityPolicy.ValidateFileName(fileName);
        return new MicrosoftUpdateCatalogDownload
        {
            DownloadUrl = sourceUri.AbsoluteUri,
            FileName = fileName,
            Sha1 = DecodeBase64Hash(properties.GetValueOrDefault("digest"), "SHA1", logger),
            Sha256 = DecodeBase64Hash(properties.GetValueOrDefault("sha256"), "SHA256", logger),
            Architectures = properties.GetValueOrDefault("architectures") ?? string.Empty,
            Languages = properties.GetValueOrDefault("languages") ?? string.Empty
        };
    }

    private static string DecodeBase64Hash(string? value, string algorithm, ILogger<MicrosoftUpdateCatalogClient> logger)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            byte[] digest = Convert.FromBase64String(value);
            if (digest.Length != (algorithm == "SHA256" ? 32 : 20))
            {
                throw new InvalidDataException("Microsoft Update Catalog digest length is invalid.");
            }
            return Convert.ToHexString(digest);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Microsoft Update Catalog digest encoding is invalid.", ex);
        }
    }

    private static string UnescapeJavascriptString(string value)
    {
        return value
            .Replace("\\'", "'", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private Task<string> SendStringAsync(string requestUri, string operationName, CancellationToken cancellationToken)
    {
        return HttpTextFetcher.SendStringWithRetryAsync(HttpClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            ApplyNoCacheHeaders(request);
            return request;
        }, _logger, operationName, cancellationToken);
    }

    private Task<string> SendFormAsync(string requestUri, IReadOnlyList<KeyValuePair<string, string>> formValues,
        string operationName, CancellationToken cancellationToken)
    {
        return HttpTextFetcher.SendStringWithRetryAsync(HttpClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = new FormUrlEncodedContent(formValues) };
            ApplyNoCacheHeaders(request);
            return request;
        }, _logger, operationName, cancellationToken);
    }

    private static void ApplyNoCacheHeaders(HttpRequestMessage request)
    {
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true
        };
        request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
    }

    private static MicrosoftUpdateCatalogUpdate? ParseUpdate(HtmlNode row)
    {
        HtmlNodeCollection? cells = row.SelectNodes("td");
        if (cells is null || cells.Count < 8)
        {
            return null;
        }

        HtmlNodeCollection? inputNodes = cells[7].SelectNodes(".//input");
        string updateId = inputNodes?.FirstOrDefault()?.GetAttributeValue("id", string.Empty) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(updateId))
        {
            return null;
        }

        HtmlNodeCollection? sizeSpans = cells[6].SelectNodes(".//span");
        string size = sizeSpans?.FirstOrDefault() is HtmlNode sizeNode
            ? HtmlEntity.DeEntitize(sizeNode.InnerText).Trim()
            : HtmlEntity.DeEntitize(cells[6].InnerText).Trim();
        long sizeInBytes = sizeSpans is not null && sizeSpans.Count > 1
            ? ParseLong(HtmlEntity.DeEntitize(sizeSpans[1].InnerText).Trim())
            : 0L;

        return new MicrosoftUpdateCatalogUpdate
        {
            UpdateId = updateId,
            Title = HtmlEntity.DeEntitize(cells[1].InnerText).Trim(),
            Products = HtmlEntity.DeEntitize(cells[2].InnerText).Trim(),
            Classification = HtmlEntity.DeEntitize(cells[3].InnerText).Trim(),
            LastUpdated = ParseDate(HtmlEntity.DeEntitize(cells[4].InnerText).Trim()),
            Version = HtmlEntity.DeEntitize(cells[5].InnerText).Trim(),
            Size = size,
            SizeInBytes = sizeInBytes
        };
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset invariantParsed))
        {
            return invariantParsed;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeLocal, out DateTimeOffset enUsParsed))
        {
            return enUsParsed;
        }

        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static long ParseLong(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : 0L;
    }
}
