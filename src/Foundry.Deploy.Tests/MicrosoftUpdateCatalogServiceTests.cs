// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.System;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class MicrosoftUpdateCatalogServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DriverDownload_StagesAndExpandsExactlyOneCab(bool preferredCacheAvailable)
    {
        using TempDirectory temp = TempDirectory.Create();
        string rawDirectory = Path.Combine(temp.Path, "raw");
        string cacheDirectory = Path.Combine(temp.Path, "cache");
        var catalogClient = new FakeMicrosoftUpdateCatalogClient();
        var downloadService = new CapturingArtifactDownloadService();
        var extraction = new FakeArchiveExtractionService();
        var service = new MicrosoftUpdateCatalogDriverService(
            extraction,
            catalogClient,
            downloadService,
            NullLogger<MicrosoftUpdateCatalogDriverService>.Instance,
            new PayloadCachePlacementService(downloadService, new AvailableStorage(cacheDirectory, preferredCacheAvailable)));

        MicrosoftUpdateCatalogDriverResult result = await service.DownloadAsync(
            CreateHardwareProfile(),
            new OperatingSystemCatalogItem { ReleaseId = "24H2", Architecture = "x64" },
            rawDirectory,
            cacheDirectory,
            TestContext.Current.CancellationToken);

        await service.ExpandAsync(rawDirectory, Path.Combine(temp.Path, "extracted"), TestContext.Current.CancellationToken);
        Assert.Single(Directory.EnumerateFiles(rawDirectory, "*.cab", SearchOption.AllDirectories));
        Assert.Equal(1, extraction.ExtractionCount);
        AssertCacheIsOutsideStaging(downloadService, rawDirectory, cacheDirectory, preferredCacheAvailable);
        string expectedRawPath = Path.Combine(rawDirectory, "update-1", "driver-amd64.cab");
        Assert.True(result.IsPayloadAvailable);
        Assert.Equal(new string('B', 64), downloadService.ExpectedHash);
        Assert.Equal("MicrosoftUpdateCatalogDriver", downloadService.ArtifactKind);
        Assert.True(File.Exists(expectedRawPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirmwareDownload_StagesAndExpandsExactlyOneCab(bool preferredCacheAvailable)
    {
        using TempDirectory temp = TempDirectory.Create();
        string rawDirectory = Path.Combine(temp.Path, "raw");
        string extractedDirectory = Path.Combine(temp.Path, "extracted");
        string cacheDirectory = Path.Combine(temp.Path, "cache");
        var catalogClient = new FakeMicrosoftUpdateCatalogClient();
        var downloadService = new CapturingArtifactDownloadService();
        var extraction = new FakeArchiveExtractionService();
        var service = new MicrosoftUpdateCatalogFirmwareService(
            extraction,
            catalogClient,
            downloadService,
            NullLogger<MicrosoftUpdateCatalogFirmwareService>.Instance,
            new PayloadCachePlacementService(downloadService, new AvailableStorage(cacheDirectory, preferredCacheAvailable)));

        MicrosoftUpdateCatalogFirmwareResult result = await service.DownloadAsync(
            new HardwareProfile { SystemFirmwareHardwareId = "UEFI\\RES_{FIRMWARE}" },
            "x64",
            rawDirectory,
            extractedDirectory,
            cacheDirectory,
            TestContext.Current.CancellationToken);

        Assert.Single(Directory.EnumerateFiles(rawDirectory, "*.cab", SearchOption.AllDirectories));
        Assert.Equal(1, extraction.ExtractionCount);
        AssertCacheIsOutsideStaging(downloadService, rawDirectory, cacheDirectory, preferredCacheAvailable);
        string expectedRawPath = Path.Combine(rawDirectory, "update-1", "driver-amd64.cab");
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new string('B', 64), downloadService.ExpectedHash);
        Assert.Equal("MicrosoftUpdateCatalogFirmware", downloadService.ArtifactKind);
        Assert.True(File.Exists(expectedRawPath));
    }

    private static void AssertCacheIsOutsideStaging(CapturingArtifactDownloadService downloadService, string stagingRoot, string cacheRoot, bool preferredCacheAvailable)
    {
        Assert.NotNull(downloadService.Artifact);
        Assert.NotNull(downloadService.DestinationPath);
        string relativePath = Path.GetRelativePath(stagingRoot, downloadService.DestinationPath);
        Assert.StartsWith($"..{Path.DirectorySeparatorChar}", relativePath, StringComparison.Ordinal);
        Assert.Equal(downloadService.Artifact.CacheKey, new DirectoryInfo(Path.GetDirectoryName(downloadService.DestinationPath)!).Name);
        if (preferredCacheAvailable)
        {
            Assert.Equal(Path.Combine(cacheRoot, "update-1", downloadService.Artifact.CacheKey, "driver-amd64.cab"), downloadService.DestinationPath);
        }
    }

    private static HardwareProfile CreateHardwareProfile()
    {
        return new HardwareProfile
        {
            PnpDevices =
            [
                new PnpDeviceInfo
                {
                    Name = "Network adapter",
                    DeviceId = @"PCI\VEN_8086&DEV_15B7&SUBSYS_00000000",
                    HardwareIds = [@"PCI\VEN_8086&DEV_15B7"],
                    PnpClass = "Net"
                }
            ]
        };
    }

    private sealed class FakeMicrosoftUpdateCatalogClient : IMicrosoftUpdateCatalogClient
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<MicrosoftUpdateCatalogUpdate>> SearchAsync(
            string searchQuery,
            bool descending = true,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MicrosoftUpdateCatalogUpdate> updates =
            [
                new MicrosoftUpdateCatalogUpdate
                {
                    UpdateId = "update-1",
                    Title = "Driver update",
                    Version = "1.0",
                    Size = "1 MB"
                }
            ];

            return Task.FromResult(updates);
        }

        public Task<IReadOnlyList<MicrosoftUpdateCatalogDownload>> GetDownloadsAsync(
            string updateId,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads =
            [
                new MicrosoftUpdateCatalogDownload
                {
                    CatalogRevision = "trusted-revision",
                    DownloadUrl = "https://example.test/driver-amd64.cab",
                    FileName = "driver-amd64.cab",
                    Sha1 = new string('A', 40),
                    Sha256 = new string('B', 64)
                }
            ];

            return Task.FromResult(downloads);
        }
    }

    private sealed class CapturingArtifactDownloadService : IArtifactDownloadService
    {
        public string? DestinationPath { get; private set; }
        public string? ExpectedHash { get; private set; }
        public string? ArtifactKind { get; private set; }

        public ArtifactIdentity? Artifact { get; private set; }
        public Task<ArtifactDownloadResult?> TryUseCachedAsync(ArtifactIdentity artifact, string path, CancellationToken cancellationToken = default) => Task.FromResult<ArtifactDownloadResult?>(null);
        public async Task<ArtifactDownloadResult> DownloadAsync(
            ArtifactIdentity artifact,
            string destinationPath,
            CancellationToken cancellationToken = default,
            IProgress<DownloadProgress>? progress = null)
        {
            DestinationPath = destinationPath;
            Artifact = artifact;
            ExpectedHash = artifact.Integrity.Digest?.Hex;
            ArtifactKind = artifact.Kind;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(destinationPath, "cab", cancellationToken).ConfigureAwait(false);

            return new ArtifactDownloadResult
            {
                DestinationPath = destinationPath,
                Downloaded = true,
                Method = "test",
                SizeBytes = 3
            };
        }
    }

    private sealed class AvailableStorage(string preferredRoot, bool preferredAvailable) : IVolumeStorageProbe
    {
        public VolumeStorageStatus Inspect(string directory) => !preferredAvailable &&
            directory.StartsWith(preferredRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? new(false, false, null) : new(true, true, 1024L * 1024 * 1024);
    }

    private sealed class FakeArchiveExtractionService : IArchiveExtractionService
    {
        public int ExtractionCount { get; private set; }
        public Task ExtractWithSevenZipAsync(
            string archivePath,
            string extractedPath,
            string workingDirectory,
            CancellationToken cancellationToken = default,
            IProgress<double>? progress = null)
        {
            ExtractionCount++;
            Directory.CreateDirectory(extractedPath);
            File.WriteAllText(Path.Combine(extractedPath, "driver.inf"), "; test");
            return Task.CompletedTask;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            return new TempDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"foundry-muc-{Guid.NewGuid():N}"));
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
