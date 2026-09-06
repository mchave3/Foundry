// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Services.DriverPacks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class MicrosoftUpdateCatalogClientTests
{
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
}
