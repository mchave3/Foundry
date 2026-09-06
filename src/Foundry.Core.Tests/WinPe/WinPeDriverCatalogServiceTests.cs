// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeDriverCatalogServiceTests
{
    [Theory]
    [InlineData("http://example.test/catalog.xml", false)]
    [InlineData("https://example.test/catalog.xml", true)]
    public async Task GetCatalogAsync_InsecureOrOversizedMetadata_IsRejected(string source, bool expectedRequest)
    {
        var handler = new OversizedCatalogHandler();
        var service = new WinPeDriverCatalogService(new HttpClient(handler));
        WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>> result = await service.GetCatalogAsync(
            new WinPeDriverCatalogOptions { CatalogUri = source, Architecture = WinPeArchitecture.X64 }, TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedRequest ? 1 : 0, handler.RequestCount);
    }

    private sealed class OversizedCatalogHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 33 * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task GetCatalogAsync_WhenHttpRequestTimesOut_ReturnsNetworkTimeoutDiagnostic()
    {
        using var httpClient = new HttpClient(new TimeoutHttpMessageHandler());
        var service = new WinPeDriverCatalogService(httpClient);

        WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>> result = await service.GetCatalogAsync(
            new WinPeDriverCatalogOptions
            {
                CatalogUri = "https://example.test/catalog.xml",
                Architecture = WinPeArchitecture.X64
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.DriverCatalogFetchFailed, result.Error?.Code);
        Assert.Equal(WinPeFailureKinds.Network, result.Error?.FailureKind);
        Assert.Equal(WinPeFailureReasons.Timeout, result.Error?.FailureReason);
        Assert.IsType<TaskCanceledException>(result.Error?.Exception);
    }

    [Fact]
    public async Task GetCatalogAsync_FiltersArchitectureReleaseVendorAndPreviewPackages()
    {
        string catalogPath = Path.Combine(Path.GetTempPath(), $"foundry-driver-catalog-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(catalogPath, """
                                                <Catalog>
                                                  <DriverPack id="dell-x64" manufacturer="Dell" name="Dell WinPE11 package" version="1.0" downloadUrl="https://example.test/dell.cab" fileName="dell.cab" format="cab" releaseDate="2026-01-01">
                                                    <OsInfo releaseId="11/24H2" architecture="x64" />
                                                    <Hashes sha256="abc" />
                                                  </DriverPack>
                                                  <DriverPack id="hp-arm64" manufacturer="HP" name="HP WinPE11 package" version="1.0" downloadUrl="https://example.test/hp.cab" fileName="hp.cab" format="cab" releaseDate="2026-01-02">
                                                    <OsInfo releaseId="11" architecture="arm64" />
                                                    <Hashes sha256="def" />
                                                  </DriverPack>
                                                  <DriverPack id="dell-preview" manufacturer="Dell" name="Dell preview WinPE11 package" version="2.0-preview" downloadUrl="https://example.test/preview.cab" fileName="preview.cab" format="cab" releaseDate="2026-01-03">
                                                    <OsInfo releaseId="11" architecture="x64" />
                                                  </DriverPack>
                                                </Catalog>
                                                """);

        var service = new WinPeDriverCatalogService();

        try
        {
            WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>> result = await service.GetCatalogAsync(
                new WinPeDriverCatalogOptions
                {
                    CatalogUri = catalogPath,
                    Architecture = WinPeArchitecture.X64,
                    Vendors = [WinPeVendorSelection.Dell],
                    RequiredWinPeReleaseId = "11"
                },
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Details);
            WinPeDriverCatalogEntry entry = Assert.Single(result.Value!);
            Assert.Equal("dell-x64", entry.Id);
            Assert.Equal(WinPeVendorSelection.Dell, entry.Vendor);
            Assert.Equal(WinPeArchitecture.X64, entry.Architecture);
        }
        finally
        {
            File.Delete(catalogPath);
        }
    }

    [Fact]
    public async Task GetCatalogAsync_ParsesWifiSupplementMetadata()
    {
        string catalogPath = Path.Combine(Path.GetTempPath(), $"foundry-driver-catalog-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(catalogPath, """
                                                <Catalog>
                                                  <DriverPack id="intel-wifi" manufacturer="Intel" name="Intel Wi-Fi supplement" version="1.0" downloadUrl="https://example.test/intel.zip" fileName="intel.zip" format="zip" packageRole="WifiSupplement" driverFamily="IntelWireless" releaseDate="2026-01-01">
                                                    <OsInfo releaseId="11" architecture="amd64" />
                                                    <Hashes sha256="abc" />
                                                  </DriverPack>
                                                </Catalog>
                                                """);

        var service = new WinPeDriverCatalogService();

        try
        {
            WinPeResult<IReadOnlyList<WinPeDriverCatalogEntry>> result = await service.GetCatalogAsync(
                new WinPeDriverCatalogOptions
                {
                    CatalogUri = catalogPath,
                    Architecture = WinPeArchitecture.X64,
                    Vendors = [WinPeVendorSelection.Intel]
                },
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Details);
            WinPeDriverCatalogEntry entry = Assert.Single(result.Value!);
            Assert.Equal(WinPeDriverPackageRole.WifiSupplement, entry.PackageRole);
            Assert.Equal(WinPeDriverFamily.IntelWireless, entry.DriverFamily);
        }
        finally
        {
            File.Delete(catalogPath);
        }
    }

    private sealed class TimeoutHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated HTTP timeout."));
    }
}
