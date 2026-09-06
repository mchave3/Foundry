// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Download;
using Foundry.Utilities.Networking;

namespace Foundry.Deploy.Tests;

public sealed class PayloadCachePlacementServiceTests
{
    [Fact]
    public async Task VerifiedReadOnlyCacheHit_DoesNotProbeCapacityOrWrite()
    {
        ArtifactIdentity artifact = CreateArtifact(6L * 1024 * 1024 * 1024);
        var downloads = new CacheLookup(hit: true);
        var probe = new StorageProbe(new VolumeStorageStatus(false, false, null));
        var service = new PayloadCachePlacementService(downloads, probe);

        PayloadCachePlacement placement = await service.ResolveAsync(artifact, "usb", "target", TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine("usb", artifact.CacheKey, artifact.FileName), placement.Path);
        Assert.True(placement.IsValidatedCacheHit);
        Assert.False(placement.UsesTargetStorage);
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, 100L)]
    [InlineData(true, true, null)]
    [InlineData(true, true, 9L)]
    public async Task UnavailablePreferredStorage_SelectsValidatedTargetCapacity(bool present, bool writable, long? available)
    {
        var probe = new StorageProbe(new VolumeStorageStatus(present, writable, available), new(true, true, 100));
        var service = new PayloadCachePlacementService(new CacheLookup(false), probe);
        ArtifactIdentity artifact = CreateArtifact(10);

        PayloadCachePlacement result = await service.ResolveAsync(artifact, "usb", "target", TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine("target", artifact.CacheKey, artifact.FileName), result.Path);
        Assert.True(result.UsesTargetStorage);
        Assert.False(result.IsValidatedCacheHit);
    }

    [Fact]
    public async Task UnknownCapacityWithoutFallback_FailsPrecisely()
    {
        var service = new PayloadCachePlacementService(new CacheLookup(false), new StorageProbe(new VolumeStorageStatus(true, true, null)));
        IOException error = await Assert.ThrowsAsync<IOException>(() => service.ResolveAsync(CreateArtifact(null), "usb", null, TestContext.Current.CancellationToken));
        Assert.Contains("capacity is unknown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyValidCachePath_IsReusedReadOnly()
    {
        ArtifactIdentity artifact = CreateArtifact(10);
        var lookup = new CacheLookup(false) { LegacyHit = Path.Combine("usb", artifact.FileName) };
        var probe = new StorageProbe(new VolumeStorageStatus(false, false, null));

        PayloadCachePlacement result = await new PayloadCachePlacementService(lookup, probe)
            .ResolveAsync(artifact, "usb", null, TestContext.Current.CancellationToken);

        Assert.Equal(lookup.LegacyHit, result.Path);
        Assert.True(result.IsValidatedCacheHit);
        Assert.Equal(0, probe.Calls);
    }

    private static ArtifactIdentity CreateArtifact(long? size) => new("revision", "source", new Uri("https://example.test/image.esd"),
        "image.esd", new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, new string('A', 64)), size), "OperatingSystemImage", null);

    private sealed class StorageProbe(params VolumeStorageStatus[] results) : IVolumeStorageProbe
    {
        public int Calls { get; private set; }
        public VolumeStorageStatus Inspect(string directory) => results[Math.Min(Calls++, results.Length - 1)];
    }

    private sealed class CacheLookup(bool hit) : IArtifactDownloadService
    {
        public string? LegacyHit { get; init; }
        public Task<ArtifactDownloadResult?> TryUseCachedAsync(ArtifactIdentity artifact, string path, CancellationToken cancellationToken = default)
            => Task.FromResult(hit || path == LegacyHit ? new ArtifactDownloadResult
            { DestinationPath = path, Downloaded = false, Method = "cache-hit", SizeBytes = artifact.Integrity.SizeBytes ?? 10 } : null);

        public Task<ArtifactDownloadResult> DownloadAsync(ArtifactIdentity artifact, string path, CancellationToken cancellationToken = default, IProgress<DownloadProgress>? progress = null)
            => throw new InvalidOperationException("Placement must not download payloads.");
    }
}
