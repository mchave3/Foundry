// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Foundry.Utilities.Networking;

namespace Foundry.Deploy.Services.Download;

/// <summary>Identifies payload bytes using metadata acquired through an authenticated catalog channel.</summary>
public sealed record ArtifactIdentity(
    string CatalogRevision,
    string SourceId,
    Uri SourceUri,
    string FileName,
    FileIntegrity Integrity,
    string Kind,
    string? ExpectedPublisher)
{
    /// <summary>Names the embedded, independently qualified vendor policy rather than a catalog-supplied signer.</summary>
    public string? PackageFamily { get; init; }
    public string? PackageId { get; init; }
    public string? PackageVersion { get; init; }

    /// <summary>Separates persistent artifacts by their complete canonical source and integrity identity.</summary>
    public string CacheKey
    {
        get
        {
            ArtifactIntegrityPolicy.ValidateIdentity(this);
            string canonical = JsonSerializer.Serialize(new[]
            {
                CatalogRevision, SourceId, SourceUri.AbsoluteUri, FileName, Kind, ExpectedPublisher, PackageFamily, PackageId, PackageVersion,
                Integrity.Digest?.Algorithm.Name, Integrity.Digest?.Hex.ToUpperInvariant(),
                Integrity.SizeBytes?.ToString(CultureInfo.InvariantCulture)
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }
}
