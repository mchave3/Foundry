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
}
