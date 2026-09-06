// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Services.DriverPacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class MicrosoftUpdateCatalogClientTests
{
    [Theory]
    [InlineData("<div id='errorPageDisplayedError'>Service error</div>")]
    [InlineData("<html><body>Unexpected gateway response</body></html>")]
    [InlineData("<table id='ctl00_catalogBody_updateMatches'><tr id='headerRow'><td>Title</td></tr></table>")]
    public async Task SearchAsync_InvalidCatalogResponseIsAnOperationError(string html)
    {
        using var http = new HttpClient(new HtmlHandler(html));
        var client = new MicrosoftUpdateCatalogClient(NullLogger<MicrosoftUpdateCatalogClient>.Instance, http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync("24H2+device", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAsync_ExplicitNoResultsRemainsAnEmptyResult()
    {
        using var http = new HttpClient(new HtmlHandler("<div id='ctl00_catalogBody_noResultText'>No results</div>"));
        var client = new MicrosoftUpdateCatalogClient(NullLogger<MicrosoftUpdateCatalogClient>.Instance, http);

        Assert.Empty(await client.SearchAsync("24H2+device", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAsync_UnexpectedResponseDoesNotExposeHardwareQueryInLogsOrError()
    {
        const string query = @"24H2+UEFI\RES_{PRIVATE_FIRMWARE_SENTINEL}";
        using var http = new HttpClient(new HtmlHandler("<html>Unexpected response</html>"));
        var logger = new CapturingLogger();
        var client = new MicrosoftUpdateCatalogClient(logger, http);

        Exception? error = await Record.ExceptionAsync(() => client.SearchAsync(query, cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("PRIVATE_FIRMWARE_SENTINEL", string.Join("\n", logger.Messages) + error);
        Assert.IsType<HttpRequestException>(error);
    }

    [Fact]
    public async Task GetDownloadsAsync_ExplicitCatalogErrorIsAnOperationError()
    {
        using var http = new HttpClient(new HtmlHandler("<div id='errorPageDisplayedError'>Service error</div>"));
        var client = new MicrosoftUpdateCatalogClient(NullLogger<MicrosoftUpdateCatalogClient>.Instance, http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetDownloadsAsync("update", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AQID")]
    public void ParseDownloads_MalformedDeclaredDigestFailsClosed(string value)
    {
        string html = $"downloadInformation[0].files[0].url = 'https://example.test/driver.cab'; downloadInformation[0].files[0].sha256 = '{value}';";
        Assert.Throws<InvalidDataException>(() => MicrosoftUpdateCatalogClient.ParseDownloads(html, NullLogger<MicrosoftUpdateCatalogClient>.Instance));
    }

    [Fact]
    public void ParseDownloads_DecodesBase64HashesAndFileName()
    {
        const string html = """
                            <script type="text/javascript">
                            downloadInformation[0].files[0] = new Object();
                            downloadInformation[0].files[0].url = 'https://catalog.s.download.windowsupdate.com/c/msdownload/update/software/updt/2026/03/sample.cab';
                            downloadInformation[0].files[0].digest = 'iH8eDpa5pkZtyW2IzGWCDGhJ0e0=';
                            downloadInformation[0].files[0].sha256 = 'yxqvwrIfOgfWwAIVL2K6czq0FTGN7ZTA/iSmlyNZbO8=';
                            downloadInformation[0].files[0].fileName = 'sample.cab';
                            downloadInformation[0].files[0].architectures = 'AMD64';
                            downloadInformation[0].files[0].languages = 'en';
                            </script>
                            """;

        IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads = MicrosoftUpdateCatalogClient.ParseDownloads(html, NullLogger<MicrosoftUpdateCatalogClient>.Instance);

        MicrosoftUpdateCatalogDownload download = Assert.Single(downloads);
        Assert.Equal("https://catalog.s.download.windowsupdate.com/c/msdownload/update/software/updt/2026/03/sample.cab", download.DownloadUrl);
        Assert.Equal("sample.cab", download.FileName);
        Assert.Equal("887F1E0E96B9A6466DC96D88CC65820C6849D1ED", download.Sha1);
        Assert.Equal("CB1AAFC2B21F3A07D6C002152F62BA733AB415318DED94C0FE24A69723596CEF", download.Sha256);
        Assert.Equal("AMD64", download.Architectures);
        Assert.Equal("en", download.Languages);
    }

    [Fact]
    public void ParseDownloads_WhenSha256IsEmpty_KeepsSha1()
    {
        const string html = """
                            downloadInformation[0].files[0].url = 'https://example.test/driver.cab';
                            downloadInformation[0].files[0].digest = 'iH8eDpa5pkZtyW2IzGWCDGhJ0e0=';
                            downloadInformation[0].files[0].sha256 = '';
                            downloadInformation[0].files[0].fileName = 'driver.cab';
                            """;

        MicrosoftUpdateCatalogDownload download = Assert.Single(
            MicrosoftUpdateCatalogClient.ParseDownloads(html, NullLogger<MicrosoftUpdateCatalogClient>.Instance));

        Assert.Equal("887F1E0E96B9A6466DC96D88CC65820C6849D1ED", download.Sha1);
        Assert.Equal(string.Empty, download.Sha256);
    }

    [Fact]
    public void ParseDownloads_ParsesMultipleFiles()
    {
        const string html = """
                            downloadInformation[0].files[0].url = 'https://example.test/driver-x64.cab';
                            downloadInformation[0].files[0].fileName = 'driver-x64.cab';
                            downloadInformation[0].files[1].url = 'https://example.test/driver-arm64.cab';
                            downloadInformation[0].files[1].fileName = 'driver-arm64.cab';
                            """;

        IReadOnlyList<MicrosoftUpdateCatalogDownload> downloads = MicrosoftUpdateCatalogClient.ParseDownloads(html, NullLogger<MicrosoftUpdateCatalogClient>.Instance);

        Assert.Equal(["driver-x64.cab", "driver-arm64.cab"], downloads.Select(download => download.FileName));
    }

    private sealed class HtmlHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) });
    }

    private sealed class CapturingLogger : ILogger<MicrosoftUpdateCatalogClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
