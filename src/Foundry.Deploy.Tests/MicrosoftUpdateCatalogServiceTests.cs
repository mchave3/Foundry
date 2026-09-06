// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;
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
    public async Task DriverDownload_PublishesExactlyOneInspectedPackage(bool preferredCacheAvailable)
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
            Target,
            rawDirectory,
            cacheDirectory,
            TestContext.Current.CancellationToken);

        await service.ExpandAsync(rawDirectory, Path.Combine(temp.Path, "extracted"), TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateFiles(rawDirectory, "*.cab", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(rawDirectory, "*.inf", SearchOption.AllDirectories));
        Assert.Equal(1, extraction.ExtractionCount);
        AssertCacheIsOutsideStaging(downloadService, rawDirectory, cacheDirectory, preferredCacheAvailable);
        Assert.True(result.IsPayloadAvailable);
        Assert.Equal(CabDigest, downloadService.ExpectedHash);
        Assert.Equal("MicrosoftUpdateCatalogDriver", downloadService.ArtifactKind);
        await service.DownloadAsync(CreateHardwareProfile(), Target, rawDirectory, cacheDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(1, downloadService.DownloadCount);
        Assert.Equal(2, extraction.ExtractionCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirmwareDownload_PublishesExactlyOneInspectedPackage(bool preferredCacheAvailable)
    {
        using TempDirectory temp = TempDirectory.Create();
        string rawDirectory = Path.Combine(temp.Path, "raw");
        string extractedDirectory = Path.Combine(temp.Path, "extracted");
        string cacheDirectory = Path.Combine(temp.Path, "cache");
        var catalogClient = new FakeMicrosoftUpdateCatalogClient();
        var downloadService = new CapturingArtifactDownloadService();
        var extraction = new FakeArchiveExtractionService(firmware: true);
        var service = new MicrosoftUpdateCatalogFirmwareService(
            extraction,
            catalogClient,
            downloadService,
            NullLogger<MicrosoftUpdateCatalogFirmwareService>.Instance,
            new PayloadCachePlacementService(downloadService, new AvailableStorage(cacheDirectory, preferredCacheAvailable)));

        MicrosoftUpdateCatalogFirmwareResult result = await service.DownloadAsync(
            new HardwareProfile { SystemFirmwareHardwareId = FirmwareId },
            Target,
            rawDirectory,
            extractedDirectory,
            cacheDirectory,
            TestContext.Current.CancellationToken);

        Assert.Empty(Directory.EnumerateFiles(rawDirectory, "*.cab", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(extractedDirectory, "*.inf", SearchOption.AllDirectories));
        Assert.Equal(1, extraction.ExtractionCount);
        AssertCacheIsOutsideStaging(downloadService, rawDirectory, cacheDirectory, preferredCacheAvailable);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(CabDigest, downloadService.ExpectedHash);
        Assert.Equal("MicrosoftUpdateCatalogFirmware", downloadService.ArtifactKind);
        await service.DownloadAsync(new HardwareProfile { SystemFirmwareHardwareId = FirmwareId }, Target, rawDirectory,
            extractedDirectory, cacheDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(1, downloadService.DownloadCount);
        Assert.Equal(2, extraction.ExtractionCount);
    }

    private const string FirmwareId = @"UEFI\RES_{12345678-1234-1234-1234-123456789ABC}";
    private static string CabDigest => Convert.ToHexString(SHA256.HashData("cab"u8));
    private static OperatingSystemCatalogItem Target => new() { WindowsRelease = "11", ReleaseId = "24H2", Architecture = "x64", BuildMajor = 26100 };

    private static void AssertCacheIsOutsideStaging(CapturingArtifactDownloadService downloadService, string stagingRoot, string cacheRoot, bool preferredCacheAvailable)
    {
        Assert.NotNull(downloadService.Artifact);
        Assert.NotNull(downloadService.DestinationPath);
        string relativePath = Path.GetRelativePath(stagingRoot, downloadService.DestinationPath);
        Assert.StartsWith($"..{Path.DirectorySeparatorChar}", relativePath, StringComparison.Ordinal);
        Assert.Equal(downloadService.Artifact.CacheKey, new DirectoryInfo(Path.GetDirectoryName(downloadService.DestinationPath)!).Name);
        if (preferredCacheAvailable)
        {
            string sourceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("update-1")));
            Assert.Equal(Path.Combine(cacheRoot, sourceKey, downloadService.Artifact.CacheKey, "driver-amd64.cab"), downloadService.DestinationPath);
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
                    Sha256 = CabDigest
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
        public int DownloadCount { get; private set; }
        public async Task<ArtifactDownloadResult?> TryUseCachedAsync(ArtifactIdentity artifact, string path, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return Convert.ToHexString(SHA256.HashData(bytes)) == artifact.Integrity.Digest?.Hex
                ? new ArtifactDownloadResult { DestinationPath = path, Downloaded = false, Method = "validated fixture cache", SizeBytes = bytes.Length }
                : null;
        }
        public async Task<ArtifactDownloadResult> DownloadAsync(
            ArtifactIdentity artifact,
            string destinationPath,
            CancellationToken cancellationToken = default,
            IProgress<DownloadProgress>? progress = null)
        {
            DestinationPath = destinationPath;
            DownloadCount++;
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

    private sealed class FakeArchiveExtractionService(bool firmware = false) : IArchiveExtractionService
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
            File.WriteAllText(Path.Combine(extractedPath, "driver.inf"), $$"""
                [Version]
                Signature="$WINDOWS NT$"
                Class={{(firmware ? "Firmware" : "Net")}}
                ClassGuid={{(firmware ? "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}" : "{4d36e972-e325-11ce-bfc1-08002be10318}")}}
                [Manufacturer]
                Example=Models,NTamd64.10.0...26100
                [Models.NTamd64.10.0...26100]
                Example=Install,{{(firmware ? FirmwareId : @"PCI\VEN_8086&DEV_15B7&SUBSYS_00000000")}}
                [Install]
                """);
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
