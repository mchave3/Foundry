// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Xml.Linq;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Catalog;

namespace Foundry.Deploy.Tests;

public sealed class DriverPackCatalogServiceTests
{
    [Theory]
    [InlineData("../driver.exe", "1")]
    [InlineData("driver.exe", "invalid")]
    [InlineData("driver.exe", "-1")]
    public void ParseItem_RejectsMalformedFileIdentity(string fileName, string size)
    {
        XElement element = XElement.Parse($"""<DriverPack id="driver-1" fileName="{fileName}" sizeBytes="{size}" downloadUrl="https://example.test/driver.exe" />""");
        Assert.ThrowsAny<ArgumentException>(() => DriverPackCatalogService.ParseItem(element));
    }

    [Fact]
    public void ParseItem_PreservesModelSystemIds()
    {
        XElement element = XElement.Parse("""
            <DriverPack id="lenovo-e14" manufacturer="Lenovo" downloadUrl="https://example.test/driver.exe">
              <Models>
                <Model name="ThinkPad E14 Gen 8 Type 21Y6 21Y7" systemId="21Y6,21Y7" />
              </Models>
              <OsInfo name="Windows 11" releaseId="25H2" architecture="x64" />
            </DriverPack>
            """);

        DriverPackCatalogItem item = DriverPackCatalogService.ParseItem(element);

        Assert.Equal(["21Y6", "21Y7"], item.SystemIds);
    }

    [Theory]
    [InlineData("Dell", "BaseDriverPack", "Win", DriverPackPackageRole.System)]
    [InlineData("HP", "BaseDriverPack", "Win", DriverPackPackageRole.System)]
    [InlineData("Lenovo", "BaseDriverPack", "Win", DriverPackPackageRole.System)]
    [InlineData("Microsoft", "BaseDriverPack", "Win", DriverPackPackageRole.Unknown)]
    [InlineData("Dell", "", "Win", DriverPackPackageRole.Unknown)]
    [InlineData("Dell", "NewRole", "Win", DriverPackPackageRole.Unknown)]
    [InlineData("Dell", "BaseDriverPack", "WinPE", DriverPackPackageRole.Unknown)]
    [InlineData("Unknown", "BaseDriverPack", "Win", DriverPackPackageRole.Unknown)]
    public void ParseItem_MapsOnlyDocumentedSystemCatalogRoles(
        string manufacturer, string packageRole, string type, DriverPackPackageRole expectedRole)
    {
        XElement element = XElement.Parse($"""
            <DriverPack id="pack" manufacturer="{manufacturer}" packageRole="{packageRole}" type="{type}" downloadUrl="https://example.test/driver.exe">
              <Models><Model name="System model" /></Models>
              <OsInfo name="Windows 11" releaseId="25H2" architecture="x64" />
            </DriverPack>
            """);

        DriverPackCatalogItem item = DriverPackCatalogService.ParseItem(element);

        Assert.Equal(expectedRole, item.PackageRole);
    }

    [Theory]
    [InlineData("Surface Thunderbolt 4 Dock")]
    [InlineData("Surface Dock 2")]
    public void ParseItem_WhenMicrosoftCatalogLabelsDockAsBasePack_ClassifiesAccessory(string modelName)
    {
        XElement element = XElement.Parse($"""
            <DriverPack id="dock" manufacturer="Microsoft" packageRole="BaseDriverPack" type="Win" downloadUrl="https://example.test/dock.msi">
              <Models><Model name="{modelName}" /></Models>
              <OsInfo name="Windows 11" releaseId="25H2" architecture="arm64" />
            </DriverPack>
            """);

        DriverPackCatalogItem item = DriverPackCatalogService.ParseItem(element);

        Assert.Equal(DriverPackPackageRole.Accessory, item.PackageRole);
    }

    [Fact]
    public void ParseItem_SurfaceArm64DockCatalogFixture_PreservesReleaseAndAccessoryIdentity()
    {
        XElement element = XElement.Parse("""
            <DriverPack id="105115|SurfaceThunderbolt4DockDrivers_Win11_arm64_22000_23.033.35231.0.msi" packageId="105115" manufacturer="Microsoft" name="SurfaceThunderbolt4DockDrivers_Win11_arm64_22000_23.033.35231.0.msi" version="1.0" fileName="SurfaceThunderbolt4DockDrivers_Win11_arm64_22000_23.033.35231.0.msi" downloadUrl="https://download.microsoft.com/download/2/b/1/2b147aac-b4b5-4656-8d28-21368fa5602f/SurfaceThunderbolt4DockDrivers_Win11_arm64_22000_23.033.35231.0.msi" sizeBytes="1581056" format="msi" type="Win" packageRole="BaseDriverPack" releaseDate="2024-07-15">
              <Models><Model name="Surface Thunderbolt 4 Dock" /></Models>
              <OsInfo name="Windows 11" releaseId="21H2" build="22000" architecture="arm64" />
            </DriverPack>
            """);

        DriverPackCatalogItem item = DriverPackCatalogService.ParseItem(element);

        Assert.Equal(DriverPackPackageRole.Accessory, item.PackageRole);
        Assert.Equal("arm64", item.OsArchitecture);
        Assert.Equal("21H2", item.OsReleaseId);
        Assert.Equal(["Surface Thunderbolt 4 Dock"], item.ModelNames);
    }
}
