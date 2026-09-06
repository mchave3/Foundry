// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Foundry.Deploy.Services.Download;
using Foundry.Utilities.Networking;
using System.Net.Http;
using System.Xml.Linq;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Http;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Catalog;

public sealed class DriverPackCatalogService : IDriverPackCatalogService
{
    private const string CatalogUri = "https://raw.githubusercontent.com/foundry-osd/catalog/refs/heads/main/Cache/DriverPack/DriverPack_Unified.xml";
    private static readonly HttpClient HttpClient = AcquisitionHttpClientFactory.Create(TimeSpan.FromSeconds(20));
    private readonly ILogger<DriverPackCatalogService> _logger;

    public DriverPackCatalogService(ILogger<DriverPackCatalogService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<DriverPackCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching driver pack catalog from {CatalogUri}.", CatalogUri);
        try
        {
            string xmlContent = await HttpTextFetcher
                .GetStringWithRetryAsync(
                    HttpClient,
                    CatalogUri,
                    _logger,
                    "Driver pack catalog download",
                    cancellationToken)
                .ConfigureAwait(false);
            string revision = CatalogContentIdentity.Calculate(xmlContent);
            XDocument document = CatalogContentIdentity.ParseXml(xmlContent);

            DriverPackCatalogItem[] items = document
                .Descendants("DriverPack")
                .Select(element => ParseItem(element) with { CatalogRevision = revision })
                .Where(item => !string.IsNullOrWhiteSpace(item.DownloadUrl))
                .OrderByDescending(item => item.ReleaseDate ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Manufacturer, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _logger.LogInformation("Loaded {ItemCount} driver pack catalog entries.", items.Length);
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError("Driver pack catalog load failed. FailureType={FailureType}", ex.GetType().Name);
            throw;
        }
    }

    internal static DriverPackCatalogItem ParseItem(XElement driverPack)
    {
        XElement? osInfo = driverPack.Element("OsInfo");
        XElement? hashes = driverPack.Element("Hashes");

        IReadOnlyList<string> models = driverPack
            .Descendants("Model")
            .Select(model => (model.Attribute("name")?.Value ?? string.Empty).Trim())
            .Where(modelName => !string.IsNullOrWhiteSpace(modelName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<string> systemIds = driverPack
            .Descendants("Model")
            .SelectMany(model => (model.Attribute("systemId")?.Value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parsed = new DriverPackCatalogItem
        {
            Id = ReadAttribute(driverPack, "id"),
            PackageId = ReadAttribute(driverPack, "packageId"),
            Manufacturer = ReadAttribute(driverPack, "manufacturer"),
            Name = ReadAttribute(driverPack, "name"),
            Version = ReadAttribute(driverPack, "version"),
            FileName = string.IsNullOrEmpty(ReadAttribute(driverPack, "fileName"))
                ? Path.GetFileName(new Uri(ReadAttribute(driverPack, "downloadUrl")).LocalPath)
                : ReadAttribute(driverPack, "fileName"),
            DownloadUrl = ReadAttribute(driverPack, "downloadUrl"),
            SizeBytes = ParseLong(ReadAttribute(driverPack, "sizeBytes")),
            Format = ReadAttribute(driverPack, "format"),
            Type = ReadAttribute(driverPack, "type"),
            ReleaseDate = ParseDate(ReadAttribute(driverPack, "releaseDate")),
            OsName = ReadAttribute(osInfo, "name"),
            OsReleaseId = ReadAttribute(osInfo, "releaseId"),
            OsArchitecture = NormalizeArchitecture(ReadAttribute(osInfo, "architecture")),
            ModelNames = models,
            SystemIds = systemIds,
            Sha256 = ReadAttribute(hashes, "sha256")
        };
        ArtifactIntegrityPolicy.ValidateMetadata(parsed.Id, new Uri(parsed.DownloadUrl), parsed.FileName,
            new FileIntegrity(string.IsNullOrEmpty(parsed.Sha256) ? null : new FileDigest(HashAlgorithmName.SHA256, parsed.Sha256),
                parsed.SizeBytes == 0 ? null : parsed.SizeBytes));
        return parsed;
    }

    private static string ReadAttribute(XElement? element, string attributeName)
    {
        return (element?.Attribute(attributeName)?.Value ?? string.Empty).Trim();
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

    private static DateTimeOffset? ParseDate(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
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

}
