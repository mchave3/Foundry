// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.System;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class MicrosoftUpdateCatalogCandidateTests
{
    private const string HardwareId = @"PCI\VEN_8086&DEV_15B7&SUBSYS_12345678";
    private const string FirmwareId = @"UEFI\RES_{12345678-1234-1234-1234-123456789ABC}";

    [Theory]
    [InlineData(false, "architecture")]
    [InlineData(true, "architecture")]
    [InlineData(false, "noncab")]
    [InlineData(true, "noncab")]
    [InlineData(false, "digest")]
    [InlineData(true, "digest")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(false, "noinf")]
    [InlineData(true, "noinf")]
    [InlineData(false, "hardware")]
    [InlineData(true, "hardware")]
    [InlineData(false, "futurebuild")]
    [InlineData(true, "futurebuild")]
    [InlineData(false, "futurerelease")]
    [InlineData(true, "futurerelease")]
    public async Task IncompatibleNewest_ContinuesToOlderCompatibleUpdate(bool firmware, string problem)
    {
        using var fixture = new Fixture(firmware);
        Package newest = fixture.Add("newest", 2);
        fixture.Add("older", 1);
        switch (problem)
        {
            case "architecture": newest.Download = newest.Download with { Architectures = "arm64", FileName = "newest-arm64.cab", DownloadUrl = "https://example.test/newest-arm64.cab" }; break;
            case "noncab": newest.Download = newest.Download with { FileName = "newest.exe", DownloadUrl = "https://example.test/newest.exe" }; break;
            case "digest": newest.Download = newest.Download with { Sha256 = string.Empty }; break;
            case "malformed": fixture.Catalog.MalformedUpdateId = "newest"; break;
            case "noinf": newest.Inf = null; break;
            case "hardware": newest.Inf = ValidInf(firmware).Replace(firmware ? FirmwareId : HardwareId, @"ROOT\UNRELATED", StringComparison.Ordinal); break;
            case "futurebuild": newest.Inf = ValidInf(firmware, 28000); break;
            case "futurerelease": fixture.Catalog.Updates[0] = fixture.Catalog.Updates[0] with { Products = "Windows 11, version 25H2" }; break;
        }

        Assert.Equal("older", await fixture.RunAsync());
        Assert.All(fixture.Catalog.Queries, query => Assert.StartsWith("24H2+", query, StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Raw, "*", SearchOption.AllDirectories), path => path.Contains("newest", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleCompatiblePayloads_AreAllInspectedAndPublished(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("update", 1);
        fixture.AddPayload("update", "companion", ValidInf(firmware));

        Assert.Equal("update", await fixture.RunAsync());
        Assert.Equal(2, fixture.ExtractionCount);
        Assert.Equal(2, Directory.EnumerateFiles(fixture.Consumer, "*.inf", SearchOption.AllDirectories).Count());
        Assert.Equal(2, Directory.EnumerateFiles(fixture.Consumer, "*.cat", SearchOption.AllDirectories).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleCompanion_RejectsWholeUpdateWithoutPublishingPartialPayload(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("newest", 2);
        fixture.AddPayload("newest", "companion", null);
        fixture.Add("older", 1);

        Assert.Equal("older", await fixture.RunAsync());
        Assert.Single(Directory.EnumerateFiles(fixture.Consumer, "*.inf", SearchOption.AllDirectories));
        Assert.Equal(3, fixture.ExtractionCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task UnknownArchitecture_RequiresApplicableInf(bool firmware, bool applicable)
    {
        using var fixture = new Fixture(firmware);
        Package package = fixture.Add("generic", 1);
        package.Download = package.Download with { Architectures = string.Empty };
        if (!applicable) package.Inf = ValidInf(firmware).Replace("NTamd64", "NTarm64", StringComparison.Ordinal);

        Assert.Equal(applicable ? "generic" : null, await fixture.RunAsync());
        Assert.Equal(firmware || applicable ? 1 : 2, fixture.ExtractionCount);
    }

    [Fact]
    public async Task DriverSearch_PreservesSubsystemAndQueriesMostSpecificHardwareFirst()
    {
        using var fixture = new Fixture(false);
        fixture.Add("update", 1);

        Assert.Equal("update", await fixture.RunAsync());
        Assert.Equal($"24H2+{HardwareId}", fixture.Catalog.Queries[0]);
    }

    [Fact]
    public async Task DriverSearch_ExhaustsSpecificCandidatesBeforeAcceptingGenericMatch()
    {
        using var fixture = new Fixture(false);
        fixture.Add("generic-newest", 2).Inf = ValidInf(false).Replace(HardwareId, @"PCI\VEN_8086&DEV_15B7", StringComparison.Ordinal);
        fixture.Add("specific-older", 1);

        Assert.Equal("specific-older", await fixture.RunAsync());
        Assert.Equal($"24H2+{HardwareId}", Assert.Single(fixture.Catalog.Queries));
    }

    [Fact]
    public async Task DriverSearch_RetriesAnUpdateUnderBroaderActualHardwareIdAfterSpecificCandidatesExhaust()
    {
        using var fixture = new Fixture(false);
        fixture.Add("generic", 1).Inf = ValidInf(false).Replace(HardwareId, @"PCI\VEN_8086&DEV_15B7", StringComparison.Ordinal);

        Assert.Equal("generic", await fixture.RunAsync());
        Assert.Equal([$"24H2+{HardwareId}", @"24H2+PCI\VEN_8086&DEV_15B7"], fixture.Catalog.Queries);
    }

    [Fact]
    public async Task FirmwareWrongClass_ContinuesToActualFirmwarePackage()
    {
        using var fixture = new Fixture(true);
        fixture.Add("driver", 2).Inf = ValidInf(false).Replace(HardwareId, FirmwareId, StringComparison.Ordinal);
        fixture.Add("firmware", 1);

        Assert.Equal("firmware", await fixture.RunAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedStagedBytes_FailBeforeExtraction(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("update", 1);
        fixture.TamperPayload = true;

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync());
        Assert.Equal(0, fixture.ExtractionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AcquisitionFailure_RemainsOperationFailure(bool firmware, bool network)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("newest", 2);
        fixture.Add("older", 1);
        Exception error = network ? new HttpRequestException("network failure") : new IOException("storage failure");
        fixture.DownloadError = error;

        Assert.Same(error, await Record.ExceptionAsync(() => fixture.RunAsync()));
        Assert.Single(fixture.Catalog.DownloadRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchFailure_IsNotHiddenByAvailabilityProbe(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Catalog.Available = false;
        var error = new HttpRequestException("catalog unavailable");
        fixture.Catalog.SearchError = error;

        Assert.Same(error, await Record.ExceptionAsync(() => fixture.RunAsync()));
    }

    [Theory]
    [InlineData(false, "../escape")]
    [InlineData(true, "../escape")]
    [InlineData(false, "C:\\outside")]
    [InlineData(true, "C:\\outside")]
    [InlineData(false, "catalog|revision")]
    [InlineData(true, "catalog|revision")]
    public async Task OpaqueCatalogId_UsesDigestDirectoryWhilePreservingNetworkIdentity(bool firmware, string id)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add(id, 1, "safe-payload");

        Assert.Equal(id, await fixture.RunAsync());
        Assert.Equal(id, Assert.Single(fixture.Catalog.DownloadRequests));
        string sourceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        Assert.Equal(sourceKey, new DirectoryInfo(Path.GetDirectoryName(fixture.DownloadPath!)!).Parent!.Name);
        Assert.All(fixture.IoPaths, path => Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false, "Windows 11, version 22H2 and later", true)]
    [InlineData(true, "Windows 11, version 22H2 and later", true)]
    [InlineData(false, "Windows 11, version 22H2", false)]
    [InlineData(true, "Windows 11, version 22H2", false)]
    [InlineData(false, "Windows 11", true)]
    [InlineData(true, "Windows 11", true)]
    public async Task BroadOrOlderCatalogCategory_RequiresExplicitRangeOrInfTargetProof(bool firmware, string products, bool applicable)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("update", 1);
        fixture.Catalog.Updates[0] = fixture.Catalog.Updates[0] with { Products = products };

        Assert.Equal(applicable ? "update" : null, await fixture.RunAsync());
        Assert.Equal(applicable ? 1 : 0, fixture.ExtractionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogOrdering_IsNewestFirstEvenWhenResponseIsUnsorted(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("older", 1);
        fixture.Add("newer", 2);

        Assert.Equal("newer", await fixture.RunAsync());
        Assert.Equal("newer", Assert.Single(fixture.Catalog.DownloadRequests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiArchitectureCab_UsesApplicableInfForSelectedTarget(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        Package package = fixture.Add("universal", 1);
        package.Download = package.Download with { Architectures = "AMD64, ARM64" };

        Assert.Equal("universal", await fixture.RunAsync());
        Assert.Equal(1, fixture.ExtractionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingDuplicateSourceMetadata_RejectsCandidateBeforeDownload(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        Package first = fixture.Add("newest", 2);
        Package conflicting = fixture.AddPayload("newest", "companion", ValidInf(firmware));
        conflicting.Download = first.Download with { Sha256 = new string('A', 64) };
        fixture.Add("older", 1);

        Assert.Equal("older", await fixture.RunAsync());
        Assert.Equal(1, fixture.ExtractionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalentArtifactWithDifferentAncillaryMetadata_IsInspectedOnce(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        Package original = fixture.Add("update", 1);
        Package duplicate = fixture.AddPayload("update", "duplicate-description", ValidInf(firmware));
        duplicate.Download = original.Download with { Languages = "fr", Architectures = "x64" };

        Assert.Equal("update", await fixture.RunAsync());
        Assert.Equal(1, fixture.ExtractionCount);
        Assert.Single(Directory.EnumerateFiles(fixture.Consumer, "*.inf", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedInf_RejectsCandidateAndContinues(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("newest", 2).Inf = ValidInf(firmware) + new string(' ', 4 * 1024 * 1024);
        fixture.Add("older", 1);

        Assert.Equal("older", await fixture.RunAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CabCannotChangeDuringExtraction(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("update", 1);
        fixture.CheckExtractionLock = true;

        Assert.Equal("update", await fixture.RunAsync());
        Assert.Equal(1, fixture.ExtractionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnownedExistingExpandedDirectory_IsNotAccepted(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("update", 1);
        fixture.PrecreateConsumerDirectory = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailure_PreservesOriginalOperationError(bool firmware)
    {
        using var fixture = new Fixture(firmware);
        fixture.Add("update", 1);
        var error = new IOException("original extraction failure");
        fixture.ExtractionError = error;

        Assert.Same(error, await Record.ExceptionAsync(() => fixture.RunAsync()));
    }

    private static string ValidInf(bool firmware, int build = 26100) => $$"""
        [Version]
        Signature="$WINDOWS NT$"
        Class={{(firmware ? "Firmware" : "Net")}}
        ClassGuid={{(firmware ? "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}" : "{4d36e972-e325-11ce-bfc1-08002be10318}")}}
        [Manufacturer]
        %Vendor%=Models,NTamd64.10.0...{{build}}
        [Models.NTamd64.10.0...{{build}}]
        %Device%=Install,{{(firmware ? FirmwareId : HardwareId)}}
        [Install]
        [Strings]
        Vendor="Example"
        Device="Example device"
        """;

    private sealed class Package(string updateId, string name, string? inf)
    {
        public string UpdateId { get; } = updateId;
        public string Name { get; } = name;
        public string? Inf { get; set; } = inf;
        public byte[] Bytes { get; } = Encoding.UTF8.GetBytes(name);
        public MicrosoftUpdateCatalogDownload Download { get; set; } = new()
        {
            CatalogRevision = "authenticated-revision",
            DownloadUrl = $"https://example.test/{name}.cab",
            FileName = $"{name}.cab",
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))),
            Architectures = "AMD64"
        };
    }

    private sealed class Fixture : IDisposable, IArtifactDownloadService, IArchiveExtractionService, IVolumeStorageProbe
    {
        private readonly bool _firmware;
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"foundry-muc-candidates-{Guid.NewGuid():N}");
        public Fixture(bool firmware) { _firmware = firmware; Directory.CreateDirectory(_root); }
        public string Raw => Path.Combine(_root, "raw");
        public string Root => _root;
        public string Consumer => _firmware ? Path.Combine(_root, "expanded") : Raw;
        public Catalog Catalog { get; } = new();
        public int ExtractionCount { get; private set; }
        public bool TamperPayload { get; set; }
        public Exception? DownloadError { get; set; }
        public List<string> IoPaths { get; } = [];
        public string? DownloadPath { get; private set; }
        public bool CheckExtractionLock { get; set; }
        public bool PrecreateConsumerDirectory { get; set; }
        public Exception? ExtractionError { get; set; }
        private FileStream? _heldOutput;

        public Package Add(string id, int day, string? name = null)
        {
            Catalog.Updates.Add(new() { UpdateId = id, Title = id, Products = "Windows 11, version 24H2", LastUpdated = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) });
            return AddPayload(id, name ?? id, ValidInf(_firmware));
        }

        public Package AddPayload(string updateId, string name, string? inf)
        {
            var package = new Package(updateId, name, inf);
            Catalog.Packages.Add(package);
            return package;
        }

        public async Task<string?> RunAsync()
        {
            var hardware = new HardwareProfile
            {
                SystemFirmwareHardwareId = FirmwareId,
                PnpDevices = [new() { Name = "Network", PnpClass = "Net", DeviceId = HardwareId + @"\INSTANCE", HardwareIds = [@"PCI\VEN_8086&DEV_15B7", HardwareId] }]
            };
            var target = new OperatingSystemCatalogItem { WindowsRelease = "11", ReleaseId = "24H2", BuildMajor = 26100, Architecture = "x64" };
            var placement = new PayloadCachePlacementService(this, this);
            if (_firmware)
            {
                var service = new MicrosoftUpdateCatalogFirmwareService(this, Catalog, this, NullLogger<MicrosoftUpdateCatalogFirmwareService>.Instance, placement);
                var result = await service.DownloadAsync(hardware, target, Raw, Consumer, Path.Combine(_root, "cache"), TestContext.Current.CancellationToken);
                return result.IsUpdateAvailable ? result.UpdateId : null;
            }
            else
            {
                var service = new MicrosoftUpdateCatalogDriverService(this, Catalog, this, NullLogger<MicrosoftUpdateCatalogDriverService>.Instance, placement);
                var result = await service.DownloadAsync(hardware, target, Raw, Path.Combine(_root, "cache"), TestContext.Current.CancellationToken);
                return result.DownloadedDrivers.FirstOrDefault()?.UpdateId;
            }
        }

        public VolumeStorageStatus Inspect(string directory) => new(true, true, 1024L * 1024 * 1024);
        public Task<ArtifactDownloadResult?> TryUseCachedAsync(ArtifactIdentity artifact, string path, CancellationToken cancellationToken = default) => Task.FromResult<ArtifactDownloadResult?>(null);
        public async Task<ArtifactDownloadResult> DownloadAsync(ArtifactIdentity artifact, string destinationPath, CancellationToken cancellationToken = default, IProgress<DownloadProgress>? progress = null)
        {
            if (DownloadError is not null) throw DownloadError;
            DownloadPath = destinationPath;
            IoPaths.Add(destinationPath);
            Package package = Catalog.Packages.First(p => p.Download.DownloadUrl == artifact.SourceUri.AbsoluteUri);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, TamperPayload ? "tampered"u8.ToArray() : package.Bytes, cancellationToken);
            return new() { DestinationPath = destinationPath, Downloaded = true, Method = "fixture", SizeBytes = package.Bytes.Length };
        }

        public async Task ExtractWithSevenZipAsync(string archivePath, string extractedPath, string workingDirectory, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
        {
            ExtractionCount++;
            IoPaths.AddRange([archivePath, extractedPath, workingDirectory]);
            if (CheckExtractionLock) Assert.Throws<IOException>(() => File.WriteAllText(archivePath, "changed"));
            string name = await File.ReadAllTextAsync(archivePath, cancellationToken);
            Package package = Catalog.Packages.Single(p => p.Name == name);
            Directory.CreateDirectory(extractedPath);
            if (PrecreateConsumerDirectory)
            {
                string cacheKey = Path.GetFileName(Path.GetDirectoryName(archivePath)!);
                string packageKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)));
                string unowned = Path.Combine(Consumer, packageKey);
                Directory.CreateDirectory(unowned);
                File.WriteAllText(Path.Combine(unowned, "unowned.inf"), "; unvalidated directory");
            }
            if (package.Inf is not null) await File.WriteAllTextAsync(Path.Combine(extractedPath, $"{name}.inf"), package.Inf, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(extractedPath, $"{name}.cat"), "companion fixture", cancellationToken);
            if (ExtractionError is not null)
            {
                _heldOutput = new FileStream(Path.Combine(extractedPath, $"{name}.cat"), FileMode.Open, FileAccess.Read, FileShare.None);
                throw ExtractionError;
            }
        }

        public void Dispose()
        {
            _heldOutput?.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Catalog : IMicrosoftUpdateCatalogClient
    {
        public List<MicrosoftUpdateCatalogUpdate> Updates { get; } = [];
        public List<Package> Packages { get; } = [];
        public List<string> Queries { get; } = [];
        public List<string> DownloadRequests { get; } = [];
        public string? MalformedUpdateId { get; set; }
        public bool Available { get; set; } = true;
        public Exception? SearchError { get; set; }
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(Available);
        public Task<IReadOnlyList<MicrosoftUpdateCatalogUpdate>> SearchAsync(string searchQuery, bool descending = true, CancellationToken cancellationToken = default)
        {
            Queries.Add(searchQuery);
            if (SearchError is not null) throw SearchError;
            return Task.FromResult<IReadOnlyList<MicrosoftUpdateCatalogUpdate>>(Updates);
        }
        public Task<IReadOnlyList<MicrosoftUpdateCatalogDownload>> GetDownloadsAsync(string updateId, CancellationToken cancellationToken = default)
        {
            DownloadRequests.Add(updateId);
            if (updateId == MalformedUpdateId)
                return Task.FromResult(MicrosoftUpdateCatalogClient.ParseDownloads("downloadInformation[0].files[0].url = 'https://example.test/a.cab'; downloadInformation[0].files[0].sha256 = 'invalid-base64';", NullLogger<MicrosoftUpdateCatalogClient>.Instance));
            return Task.FromResult<IReadOnlyList<MicrosoftUpdateCatalogDownload>>(Packages.Where(p => p.UpdateId == updateId).Select(p => p.Download).ToArray());
        }
    }
}
