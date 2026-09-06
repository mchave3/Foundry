// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Xml.Linq;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Http;
using Foundry.Deploy.Services.Download;
using Foundry.Utilities.Networking;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Catalog;

public sealed class OperatingSystemCatalogService : IOperatingSystemCatalogService
{
    private const int SupportedSchemaVersion = 4;
    private const string CatalogUri = "https://raw.githubusercontent.com/foundry-osd/catalog/refs/heads/main/Cache/OS/OperatingSystem.xml";
    private static readonly HttpClient HttpClient = AcquisitionHttpClientFactory.Create(TimeSpan.FromSeconds(20));
    private readonly ILogger<OperatingSystemCatalogService> _logger;

    public OperatingSystemCatalogService(ILogger<OperatingSystemCatalogService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<OperatingSystemCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching operating system catalog from {CatalogUri}.", CatalogUri);
        try
        {
            string xmlContent = await HttpTextFetcher
                .GetStringWithRetryAsync(
                    HttpClient,
                    CatalogUri,
                    _logger,
                    "Operating system catalog download",
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<OperatingSystemCatalogItem> parsedItems = ParseCatalog(xmlContent);

            OperatingSystemCatalogItem[] items = parsedItems
                .Where(OperatingSystemSupportMatrix.IsSupported)
                .OrderByDescending(item => item.BuildMajor)
                .ThenByDescending(item => item.BuildUbr)
                .ThenBy(item => item.Architecture, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Edition, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            int filteredCount = parsedItems.Count - items.Length;
            if (filteredCount > 0)
            {
                _logger.LogInformation(
                    "Filtered {FilteredCount} unsupported operating system entries. SupportedScope=Windows {WindowsRelease} {ReleaseIds}",
                    filteredCount,
                    OperatingSystemSupportMatrix.SupportedWindowsRelease,
                    string.Join(", ", OperatingSystemSupportMatrix.ReleaseSearchOrder));
            }

            _logger.LogInformation("Loaded {ItemCount} operating system catalog entries.", items.Length);
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError("Operating system catalog load failed. FailureType={FailureType}", ex.GetType().Name);
            throw;
        }
    }

    internal static IReadOnlyList<OperatingSystemCatalogItem> ParseCatalog(string xmlContent)
    {
        string revision = CatalogContentIdentity.Calculate(xmlContent);
        XDocument document = CatalogContentIdentity.ParseXml(xmlContent);
        XElement root = document.Root
            ?? throw new InvalidDataException("Operating system catalog root element is missing.");
        if (!int.TryParse(root.Attribute("schemaVersion")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int schemaVersion) ||
            schemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Operating system catalog schema version {SupportedSchemaVersion} is required.");
        }

        var sourcesById = new Dictionary<string, SourceMetadata>(StringComparer.Ordinal);
        foreach (XElement source in root.Element("Sources")?.Elements("Source") ?? [])
        {
            string sourceId = (source.Attribute("id")?.Value ?? string.Empty).Trim();
            string build = (source.Attribute("build")?.Value ?? string.Empty).Trim();
            string mediaDateText = (source.Attribute("mediaDate")?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sourceId) ||
                string.IsNullOrWhiteSpace(build) ||
                !int.TryParse(source.Attribute("buildMajor")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int buildMajor) ||
                !int.TryParse(source.Attribute("buildUbr")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int buildUbr) ||
                !DateOnly.TryParseExact(
                    mediaDateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly mediaDate) ||
                !sourcesById.TryAdd(sourceId, new SourceMetadata(build, buildMajor, buildUbr, mediaDate)))
            {
                throw new InvalidDataException("Operating system catalog source metadata is invalid.");
            }
        }

        IEnumerable<XElement> itemElements = root.Element("Items")?.Elements("Item") ?? [];
        return itemElements
            .Where(item => !string.IsNullOrWhiteSpace(ReadElement(item, "url")))
            .Select(item =>
            {
                string sourceId = ReadElement(item, "sourceId");
                if (!sourcesById.TryGetValue(sourceId, out SourceMetadata? source))
                {
                    throw new InvalidDataException(
                        $"Operating system catalog item references unknown source '{sourceId}'.");
                }

                OperatingSystemCatalogItem parsed = ParseItem(item, source) with { CatalogRevision = revision };
                ArtifactIntegrityPolicy.ValidateIdentity(new ArtifactIdentity(revision, parsed.SourceId, new Uri(parsed.Url), parsed.FileName,
                    new FileIntegrity(string.IsNullOrEmpty(parsed.Sha256) ? null : new FileDigest(HashAlgorithmName.SHA256, parsed.Sha256), parsed.SizeBytes == 0 ? null : parsed.SizeBytes),
                    "OperatingSystemImage", null));
                return parsed;
            })
            .ToArray();
    }

    private static OperatingSystemCatalogItem ParseItem(XElement item, SourceMetadata source)
    {
        return new OperatingSystemCatalogItem
        {
            SourceId = ReadElement(item, "sourceId"),
            ClientType = ReadElement(item, "clientType"),
            WindowsRelease = ReadElement(item, "windowsRelease"),
            ReleaseId = ReadElement(item, "releaseId"),
            Build = source.Build,
            BuildMajor = source.BuildMajor,
            BuildUbr = source.BuildUbr,
            MediaDate = source.MediaDate,
            Architecture = NormalizeArchitecture(ReadElement(item, "architecture")),
            LanguageCode = ReadElement(item, "languageCode"),
            Language = ReadElement(item, "language"),
            Edition = ReadElement(item, "edition"),
            FileName = ReadElement(item, "fileName"),
            SizeBytes = ParseLong(ReadElement(item, "sizeBytes")),
            LicenseChannel = ReadElement(item, "licenseChannel"),
            Url = ReadElement(item, "url"),
            Sha1 = ReadElement(item, "sha1"),
            Sha256 = ReadElement(item, "sha256")
        };
    }

    private static string ReadElement(XElement parent, string elementName)
    {
        return (parent.Element(elementName)?.Value ?? string.Empty).Trim();
    }

    private static long ParseLong(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) || parsed < 0)
        {
            throw new ArgumentException("Catalog size must be a nonnegative integer.");
        }
        return parsed;
    }

    private static string NormalizeArchitecture(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "amd64" => "x64",
            "aarch64" => "arm64",
            _ => normalized
        };
    }

    private sealed record SourceMetadata(string Build, int BuildMajor, int BuildUbr, DateOnly MediaDate);
}
