// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using Foundry.Deploy.Services.Download;
using Foundry.Utilities.Networking;

namespace Foundry.Deploy.Tests;

public sealed class ArtifactIntegrityPolicyTests
{
    [Theory]
    [InlineData("http://dl.delivery.mp.microsoft.com/image.esd", "SHA256", true)]
    [InlineData("http://a.dl.delivery.mp.microsoft.com/image.esd", "SHA256", true)]
    [InlineData("http://dl.delivery.mp.microsoft.com.evil.test/image.esd", "SHA256", false)]
    [InlineData("http://dl.delivery.mp.microsoft.com/image.esd", "SHA1", false)]
    [InlineData("http://example.test/image.esd", "SHA256", false)]
    [InlineData("https://example.test/image.esd", "", false)]
    public void OperatingSystemImage_RequiresStrongScopedIntegrity(string url, string algorithm, bool expected)
    {
        FileDigest? digest = algorithm.Length == 0 ? null : new(
            new HashAlgorithmName(algorithm), new string('A', algorithm == "SHA256" ? 64 : 40));
        var artifact = new ArtifactIdentity("trusted-revision", "os-1", new Uri(url), "image.esd",
            new FileIntegrity(digest, 1024), "OperatingSystemImage", null);

        Assert.Equal(expected, ArtifactIntegrityPolicy.IsAllowed(artifact));
    }

    [Theory]
    [InlineData("../image.esd", "revision", "source", "A")]
    [InlineData("C:\\image.esd", "revision", "source", "A")]
    [InlineData("CON.esd", "revision", "source", "A")]
    [InlineData("image.esd", "", "source", "A")]
    [InlineData("image.esd", "revision", "", "A")]
    [InlineData("image.esd", "revision", " source", "A")]
    [InlineData("image.esd", "revision", "source\nother", "A")]
    [InlineData("image.esd", "revision", "source", "Z")]
    public void MalformedIdentityOrDigest_IsRejected(string fileName, string revision, string source, string digit)
    {
        Assert.ThrowsAny<ArgumentException>(() => ArtifactIntegrityPolicy.Validate(new ArtifactIdentity(
            revision, source, new Uri("https://example.test/image.esd"), fileName,
            new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, string.Concat(Enumerable.Repeat(digit, 64))), 10),
            "OperatingSystemImage", null)));
    }

    [Fact]
    public void CacheKey_SeparatesRevisionSourceAndDigestWithSameFileName()
    {
        var artifact = new ArtifactIdentity("revision", "source", new Uri("https://example.test/image.esd"), "image.esd",
            new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, new string('A', 64)), 10), "OperatingSystemImage", null);

        string[] keys = [artifact.CacheKey, (artifact with { CatalogRevision = "next" }).CacheKey,
            (artifact with { SourceUri = new Uri("https://other.test/image.esd") }).CacheKey,
            (artifact with { Integrity = new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, new string('B', 64)), 10) }).CacheKey];

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("105115|SurfaceThunderbolt4DockDrivers_Win11_arm64_22000_23.033.35231.0.msi")]
    [InlineData("../source")]
    [InlineData("C:\\source")]
    public void OpaqueSourceId_IsPreservedWithoutBecomingAPath(string sourceId)
    {
        var artifact = new ArtifactIdentity("revision", sourceId, new Uri("https://example.test/image.esd"), "image.esd",
            new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, new string('A', 64)), 10), "OperatingSystemImage", null);

        ArtifactIntegrityPolicy.Validate(artifact);

        Assert.Equal(sourceId, artifact.SourceId);
        Assert.Matches("^[a-f0-9]{64}$", artifact.CacheKey);
        Assert.NotEqual(artifact.CacheKey, (artifact with { SourceId = "other" }).CacheKey);
    }

    [Fact]
    public void HashlessVendorRedirect_CannotLeaveQualifiedPublisherHost()
    {
        ArtifactIdentity artifact = ArtifactIntegrityPolicy.FromDriverPack(new Foundry.Deploy.Models.DriverPackCatalogItem
        {
            CatalogRevision = "revision",
            Id = "driver-1",
            Manufacturer = "Dell",
            FileName = "driver.exe",
            DownloadUrl = "https://downloads.dell.com/driver.exe"
        });
        Assert.Throws<InvalidDataException>(() => ArtifactIntegrityPolicy.ValidateSourceUri(artifact, new Uri("https://example.test/driver.exe")));
        Assert.Throws<InvalidDataException>(() => ArtifactIntegrityPolicy.ValidateSourceUri(artifact, new Uri("http://downloads.dell.com/driver.exe")));
    }

    [Theory]
    [InlineData(".cab")]
    [InlineData(".zip")]
    public void HashlessArchive_ReportsUnavailableIntegrity(string extension)
    {
        var item = new Foundry.Deploy.Models.DriverPackCatalogItem
        {
            CatalogRevision = "revision",
            Id = "driver-1",
            FileName = $"driver{extension}",
            DownloadUrl = $"https://example.test/driver{extension}"
        };
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ArtifactIntegrityPolicy.FromDriverPack(item));
        Assert.Contains("Integrity unavailable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySha1Policy_NeverAuthorizesEsdFiles()
    {
        var artifact = new ArtifactIdentity("revision", "source", new Uri("https://example.test/image.esd"), "image.esd",
            new FileIntegrity(new FileDigest(HashAlgorithmName.SHA1, new string('A', 40)), 10), "MicrosoftUpdateCatalogDriver", null);
        Assert.False(ArtifactIntegrityPolicy.IsAllowed(artifact));
    }
}
