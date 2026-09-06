// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Models;
using Foundry.Utilities.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class ArtifactDownloadServiceTests
{
    [Fact]
    public async Task DownloadAsync_HashlessVendorIsFreshAndSignatureCheckedBeforePublication()
    {
        using TempDirectory temp = new();
        byte[] bytes = [1, 2, 3];
        ArtifactIdentity artifact = ArtifactIntegrityPolicy.FromDriverPack(new DriverPackCatalogItem
        {
            CatalogRevision = "authenticated-revision",
            Id = "dell-package",
            PackageId = "package-1",
            Version = "A06",
            Manufacturer = "Dell",
            FileName = "driver.exe",
            DownloadUrl = "https://downloads.dell.com/driver.exe",
            SizeBytes = bytes.Length
        });
        string path = Path.Combine(temp.Path, artifact.FileName);
        await File.WriteAllBytesAsync(path, [4, 5, 6], TestContext.Current.CancellationToken);
        var handler = new StaticHttpMessageHandler(bytes);
        bool validatedBeforePublish = false;
        var service = new ArtifactDownloadService(NullLogger<ArtifactDownloadService>.Instance, new HttpClient(handler),
            async (stagedPath, subjects, token) =>
            {
                Assert.Single(subjects);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(stagedPath, token));
                Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(path, token));
                Assert.Throws<IOException>(() => File.OpenWrite(stagedPath));
                validatedBeforePublish = true;
            });

        ArtifactDownloadResult result = await service.DownloadAsync(artifact, path, TestContext.Current.CancellationToken);

        Assert.True(validatedBeforePublish);
        Assert.True(result.Downloaded);
        Assert.Equal(1, handler.RequestCount);
        Assert.Null(await service.TryUseCachedAsync(artifact, path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAsync_InvalidVendorSignatureNeverPublishesOrRetries()
    {
        using TempDirectory temp = new();
        ArtifactIdentity artifact = ArtifactIntegrityPolicy.FromDriverPack(new DriverPackCatalogItem
        {
            CatalogRevision = "authenticated-revision",
            Id = "dell-package",
            Manufacturer = "Dell",
            FileName = "driver.exe",
            DownloadUrl = "https://downloads.dell.com/driver.exe"
        });
        var handler = new StaticHttpMessageHandler([1]);
        var service = new ArtifactDownloadService(NullLogger<ArtifactDownloadService>.Instance, new HttpClient(handler),
            (_, _, _) => throw new InvalidDataException("Signature is invalid."));
        string path = Path.Combine(temp.Path, "driver.exe");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(artifact, path, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TryUseCachedAsync_WhenLegacyBytesAreValid_ReusesWithoutWritingManifest()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "install.esd");
        byte[] bytes = Encoding.UTF8.GetBytes("valid cached content");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var handler = new StaticHttpMessageHandler(bytes);
        var service = CreateService(handler);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ArtifactDownloadResult? result = await service.TryUseCachedAsync(CreateArtifact(bytes), path, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.Downloaded);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists($"{path}.manifest.json"));
    }

    [Fact]
    public async Task DownloadAsync_WhenManifestMatchesTamperedBytes_ReplacesCorruptCache()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "install.esd");
        byte[] tampered = Encoding.UTF8.GetBytes("tampered-content");
        byte[] valid = Encoding.UTF8.GetBytes("verified-content");
        ArtifactIdentity artifact = CreateArtifact(valid);
        await File.WriteAllBytesAsync(path, tampered, TestContext.Current.CancellationToken);
        DateTimeOffset lastWriteTime = DateTimeOffset.UtcNow.AddMinutes(-3);
        File.SetLastWriteTimeUtc(path, lastWriteTime.UtcDateTime);
        await File.WriteAllTextAsync($"{path}.manifest.json", JsonSerializer.Serialize(new ArtifactCacheManifest
        {
            ArtifactKind = artifact.Kind,
            SourceUrl = artifact.SourceUri.AbsoluteUri,
            HashAlgorithm = "SHA256",
            ExpectedHash = artifact.Integrity.Digest!.Hex,
            ExpectedSizeBytes = valid.Length,
            FileSizeBytes = valid.Length,
            FileLastWriteTimeUtc = lastWriteTime,
            ValidatedAtUtc = lastWriteTime
        }), TestContext.Current.CancellationToken);
        var handler = new StaticHttpMessageHandler(valid);

        ArtifactDownloadResult result = await CreateService(handler).DownloadAsync(artifact, path, TestContext.Current.CancellationToken);

        Assert.True(result.Downloaded);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(valid, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryUseCachedAsync_HashlessPersistentArtifactIsAlwaysMiss()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "driver.exe");
        byte[] bytes = Encoding.UTF8.GetBytes("untrusted");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var artifact = CreateArtifact(bytes) with { Integrity = new FileIntegrity(null, bytes.Length), Kind = "OemDriverPack", FileName = "driver.exe" };
        var handler = new StaticHttpMessageHandler(bytes);

        Assert.Null(await CreateService(handler).TryUseCachedAsync(artifact, path, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_MalformedDigestFailsBeforeFilesystemOrNetwork()
    {
        using TempDirectory temp = new();
        string destination = Path.Combine(temp.Path, "missing", "install.esd");
        var handler = new StaticHttpMessageHandler([]);
        var artifact = CreateArtifact([1]) with { Integrity = new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, new string('Z', 64)), null) };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => CreateService(handler).DownloadAsync(artifact, destination, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.RequestCount);
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
    }

    [Fact]
    public async Task DownloadAsync_InvalidReplacementPreservesPreviousBytesWithoutRetry()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "install.esd");
        byte[] previous = Encoding.UTF8.GetBytes("old payload");
        await File.WriteAllBytesAsync(path, previous, TestContext.Current.CancellationToken);
        var handler = new StaticHttpMessageHandler(Encoding.UTF8.GetBytes("bad payload"));

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateService(handler).DownloadAsync(CreateArtifact(Encoding.UTF8.GetBytes("new payload")), path, TestContext.Current.CancellationToken));

        Assert.Equal(previous, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_InventoryFailureDoesNotUndoValidPublication()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "install.esd");
        Directory.CreateDirectory($"{path}.manifest.json");
        byte[] bytes = Encoding.UTF8.GetBytes("valid payload");

        ArtifactDownloadResult result = await CreateService(new StaticHttpMessageHandler(bytes))
            .DownloadAsync(CreateArtifact(bytes), path, TestContext.Current.CancellationToken);

        Assert.True(result.Downloaded);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryUseCachedAsync_FileAccessErrorsAndCancellationAreNotCacheMisses()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "install.esd");
        byte[] bytes = [1, 2, 3];
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var service = CreateService(new StaticHttpMessageHandler(bytes));
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => service.TryUseCachedAsync(CreateArtifact(bytes), path, TestContext.Current.CancellationToken));
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TryUseCachedAsync(CreateArtifact(bytes), path, canceled.Token));
    }

    private static ArtifactIdentity CreateArtifact(byte[] content) => new("trusted-revision", "source-1", new Uri("https://example.test/install.esd"), "install.esd",
        new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, Convert.ToHexString(SHA256.HashData(content))), content.Length), "OperatingSystemImage", null);

    private static ArtifactDownloadService CreateService(HttpMessageHandler handler) => new(NullLogger<ArtifactDownloadService>.Instance, new HttpClient(handler));

    private sealed class StaticHttpMessageHandler(byte[] content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"foundry-artifact-cache-{Guid.NewGuid():N}");
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
