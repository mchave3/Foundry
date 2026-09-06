// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Services.DriverPacks;

namespace Foundry.Deploy.Tests;

public sealed class MicrosoftUpdateCatalogSupportTests
{
    [Fact]
    public void BuildReleaseSearchOrder_PutsSupportedTargetReleaseFirstWithoutDuplicates()
    {
        string[] order = MicrosoftUpdateCatalogSupport.BuildReleaseSearchOrder("24H2");

        Assert.Equal(["24H2", "25H2", "23H2"], order);
    }

    [Fact]
    public void TryExtractDriverSearchHardwareId_UsesDeviceIdBeforeHardwareIds()
    {
        var device = new PnpDeviceInfo
        {
            DeviceId = @"PCI\VEN_8086&DEV_15B7&SUBSYS_00000000",
            HardwareIds = [@"PCI\VEN_1234&DEV_5678"]
        };

        string? hardwareId = MicrosoftUpdateCatalogSupport.TryExtractDriverSearchHardwareId(device);

        Assert.Equal("VEN_8086&DEV_15B7", hardwareId);
    }

    [Fact]
    public void BuildSearchQuery_JoinsOnlyNonEmptySegments()
    {
        string query = MicrosoftUpdateCatalogSupport.BuildSearchQuery(" Surface ", "", "24H2", " x64 ");

        Assert.Equal("Surface+24H2+x64", query);
    }

    [Fact]
    public void SelectPreferredCab_PrefersExactArchitectureMatch()
    {
        MicrosoftUpdateCatalogDownload? download = MicrosoftUpdateCatalogSupport.SelectPreferredCab(
            [
                CreateDownload("https://example.test/driver-x86.cab"),
                CreateDownload("https://example.test/driver-amd64.cab"),
                CreateDownload("https://example.test/readme.txt")
            ],
            "x64");

        Assert.Equal("https://example.test/driver-amd64.cab", download?.DownloadUrl);
    }

    [Fact]
    public void SelectPreferredCab_WhenNoExactMatch_FallsBackToCompatibleCab()
    {
        MicrosoftUpdateCatalogDownload? download = MicrosoftUpdateCatalogSupport.SelectPreferredCab(
            [
                CreateDownload("https://example.test/driver-generic.cab"),
                CreateDownload("https://example.test/driver-arm64.cab")
            ],
            "x64");

        Assert.Equal("https://example.test/driver-generic.cab", download?.DownloadUrl);
    }

    private static MicrosoftUpdateCatalogDownload CreateDownload(string url)
    {
        return new MicrosoftUpdateCatalogDownload
        {
            DownloadUrl = url,
            FileName = MicrosoftUpdateCatalogSupport.ResolveFileNameFromUrl(url)
        };
    }
}
