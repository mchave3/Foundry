// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Security.Cryptography;
using Foundry.Core.Services.Security;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Utilities.Networking;

namespace Foundry.Deploy.Services.Download;

/// <summary>Defines permitted metadata, payload transport, and legacy digest boundaries for deployment.</summary>
public static class ArtifactIntegrityPolicy
{
    private const string MicrosoftContentHost = "dl.delivery.mp.microsoft.com";

    /// <summary>Tests policy without treating malformed or unavailable integrity as trusted.</summary>
    public static bool IsAllowed(ArtifactIdentity artifact)
    {
        try
        {
            Validate(artifact);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Rejects malformed identities and payloads without an explicitly supported trust path before I/O.</summary>
    public static void Validate(ArtifactIdentity artifact)
    {
        ValidateIdentity(artifact);
        FileDigest? digest = artifact.Integrity.Digest;
        bool strong = digest?.Algorithm == HashAlgorithmName.SHA256;
        bool legacyCatalogPackage = artifact.Kind is "MicrosoftUpdateCatalogDriver" or "MicrosoftUpdateCatalogFirmware" &&
            !Path.GetExtension(artifact.FileName).Equals(".esd", StringComparison.OrdinalIgnoreCase);
        bool signedFreshVendor = digest is null && artifact.Kind == "OemDriverPack" &&
            Path.GetExtension(artifact.FileName).ToLowerInvariant() is ".exe" or ".msi" &&
            artifact.SourceUri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(artifact.PackageId) &&
            artifact.PackageFamily is not null && artifact.ExpectedPublisher is not null &&
            VendorExecutableTrustPolicy.GetExpectedPublisherSubjects(artifact.PackageFamily).Contains(artifact.ExpectedPublisher);
        if (!strong && !(legacyCatalogPackage && digest?.Algorithm == HashAlgorithmName.SHA1) && !signedFreshVendor)
        {
            throw new InvalidDataException($"Integrity unavailable for {artifact.Kind}: an authenticated supported digest is required.");
        }

        ValidateSourceUri(artifact, artifact.SourceUri);
    }

    /// <summary>Validates each payload redirect with the same transport policy as its authenticated source.</summary>
    public static void ValidateSourceUri(ArtifactIdentity artifact, Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("Artifact source URI is invalid.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            if (artifact.Integrity.Digest is null && artifact.PackageFamily is not null)
            {
                VendorExecutableTrustPolicy.ValidateDownloadSource(artifact.PackageFamily, uri);
            }
            return;
        }

        bool scopedEsd = artifact.Kind == "OperatingSystemImage" &&
            Path.GetExtension(artifact.FileName).Equals(".esd", StringComparison.OrdinalIgnoreCase) &&
            artifact.Integrity.Digest?.Algorithm == HashAlgorithmName.SHA256 &&
            (uri.Host.Equals(MicrosoftContentHost, StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith($".{MicrosoftContentHost}", StringComparison.OrdinalIgnoreCase));
        if (uri.Scheme != Uri.UriSchemeHttp || !scopedEsd)
        {
            throw new InvalidDataException("Artifact transport is unavailable: authenticated HTTPS or scoped Microsoft ESD HTTP with SHA256 is required.");
        }
    }

    /// <summary>Validates source tokens, filename containment, explicit digest syntax, and exact known size.</summary>
    public static void ValidateIdentity(ArtifactIdentity artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateToken(artifact.CatalogRevision, nameof(artifact.CatalogRevision));
        ValidateToken(artifact.Kind, nameof(artifact.Kind));
        ValidateMetadata(artifact.SourceId, artifact.SourceUri, artifact.FileName, artifact.Integrity);
        if (artifact.ExpectedPublisher is not null &&
            (artifact.PackageFamily is null || !VendorExecutableTrustPolicy.GetExpectedPublisherSubjects(artifact.PackageFamily).Contains(artifact.ExpectedPublisher)))
        {
            throw new ArgumentException("Expected publisher must match an embedded qualified package family.");
        }
    }

    /// <summary>Validates raw catalog fields without assigning an authentication revision to unbound rows.</summary>
    internal static void ValidateMetadata(string sourceId, Uri sourceUri, string fileName, FileIntegrity integrity)
    {
        ValidateToken(sourceId, nameof(sourceId));
        ValidateFileName(fileName);
        ArgumentNullException.ThrowIfNull(sourceUri);
        if (!sourceUri.IsAbsoluteUri || !string.IsNullOrEmpty(sourceUri.UserInfo) || !string.IsNullOrEmpty(sourceUri.Fragment))
        {
            throw new ArgumentException("Artifact source must be an absolute URI without credentials or fragment.");
        }
        ArgumentNullException.ThrowIfNull(integrity);
        if (integrity.SizeBytes is <= 0)
        {
            throw new ArgumentException("Known artifact size must be positive.");
        }
        if (integrity.Digest is { } digest)
        {
            int length = digest.Algorithm == HashAlgorithmName.SHA256 ? 64 : digest.Algorithm == HashAlgorithmName.SHA1 ? 40 : 0;
            if (length == 0 || digest.Hex is null || digest.Hex.Length != length || !digest.Hex.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("Artifact digest must declare SHA256 or SHA1 and contain the exact hexadecimal length.");
            }
        }
    }

    /// <summary>Builds OS identity from the catalog's explicit SHA256 field; SHA1 never substitutes for it.</summary>
    public static ArtifactIdentity FromOperatingSystem(OperatingSystemCatalogItem item)
    {
        return Create(item.CatalogRevision, item.SourceId, item.Url, item.FileName,
            item.Sha256, string.Empty, item.SizeBytes, "OperatingSystemImage");
    }

    /// <summary>Builds OEM identity without interpreting unsupported or ambiguously named digest fields.</summary>
    public static ArtifactIdentity FromDriverPack(DriverPackCatalogItem item)
    {
        if (Path.GetExtension(item.FileName).ToLowerInvariant() is not (".exe" or ".msi"))
        {
            return Create(item.CatalogRevision, item.Id, item.DownloadUrl, item.FileName,
                item.Sha256, string.Empty, item.SizeBytes, "OemDriverPack");
        }

        string family = item.Manufacturer.Trim().ToLowerInvariant() switch
        {
            "dell" => "DellDriverPack",
            "lenovo" => "LenovoDriverPack",
            "microsoft" or "surface" => "SurfaceDriverPack",
            "hp" or "hewlett-packard" => "HpDriverPack",
            _ => throw new InvalidDataException("Integrity unavailable: this executable package publisher is not qualified.")
        };
        IReadOnlySet<string> publishers = VendorExecutableTrustPolicy.GetExpectedPublisherSubjects(family);
        var artifact = new ArtifactIdentity(item.CatalogRevision, item.Id, new Uri(item.DownloadUrl), item.FileName,
            new FileIntegrity(string.IsNullOrEmpty(item.Sha256) ? null : new FileDigest(HashAlgorithmName.SHA256, item.Sha256),
                item.SizeBytes == 0 ? null : item.SizeBytes), "OemDriverPack", publishers.Single())
        {
            PackageFamily = family,
            PackageId = string.IsNullOrWhiteSpace(item.PackageId) ? item.Id : item.PackageId,
            PackageVersion = item.Version
        };
        Validate(artifact);
        return artifact;
    }

    /// <summary>Allows the explicitly documented Update Catalog SHA1 field only for HTTPS non-ESD packages.</summary>
    public static ArtifactIdentity FromMicrosoftUpdate(MicrosoftUpdateCatalogUpdate update, MicrosoftUpdateCatalogDownload download, string kind)
    {
        return Create(download.CatalogRevision, update.UpdateId, download.DownloadUrl, download.FileName,
            download.Sha256, download.Sha1, 0, kind);
    }

    /// <summary>Rejects filename traversal and Windows device aliases instead of silently rewriting identity.</summary>
    public static void ValidateFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string stem = value.Split('.')[0];
        bool reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
        if (value.Length > 180 || value is "." or ".." || value != value.Trim() || value.EndsWith('.') || reserved ||
            value.Any(ch => char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch)))
        {
            throw new ArgumentException("Artifact filename is not a safe single filename.", nameof(value));
        }
    }

    private static ArtifactIdentity Create(string revision, string sourceId, string source, string fileName,
        string sha256, string legacySha1, long size, string kind)
    {
        FileDigest? digest = !string.IsNullOrWhiteSpace(sha256) ? new(HashAlgorithmName.SHA256, sha256) :
            !string.IsNullOrWhiteSpace(legacySha1) ? new(HashAlgorithmName.SHA1, legacySha1) : null;
        if (size < 0)
        {
            throw new ArgumentException("Artifact size cannot be negative.");
        }
        var artifact = new ArtifactIdentity(revision, sourceId, new Uri(source, UriKind.Absolute), fileName,
            new FileIntegrity(digest, size == 0 ? null : size), kind, null);
        Validate(artifact);
        return artifact;
    }

    private static void ValidateToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value != value.Trim() || value is "." or ".." ||
            value.Any(ch => char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch)))
        {
            throw new ArgumentException("Artifact catalog and source identity must be nonempty bounded tokens.", name);
        }
    }
}
