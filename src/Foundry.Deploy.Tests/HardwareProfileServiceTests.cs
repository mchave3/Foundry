// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Hardware;
using Foundry.Utilities.Hardware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class HardwareProfileServiceTests
{
    [Fact]
    public async Task GetCurrentAsync_MapsSnapshotAndAppliesDeployPresentationPolicy()
    {
        var snapshot = new HardwareSnapshot(
            "Hewlett-Packard",
            " ",
            "EliteBook",
            "SERIAL",
            "x64",
            false,
            true,
            true,
            "UEFI\\RES_{FIRMWARE}",
            [new PnpDeviceSnapshot("Device", "PCI\\VEN_1234", ["PCI\\VEN_1234"], "{CLASS}", "Vendor", "Net")])
        {
            FirmwareType = WindowsFirmwareType.Uefi,
            AssetTag = "ASSET-42",
            SystemUuid = "550e8400-e29b-41d4-a716-446655440000"
        };
        var service = CreateService(_ => Task.FromResult(snapshot));

        HardwareProfile profile = await service.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WindowsFirmwareType.Uefi, profile.FirmwareType);
        Assert.Equal("HP", profile.Manufacturer);
        Assert.Equal("Unknown", profile.Model);
        Assert.Equal("EliteBook", profile.Product);
        Assert.Equal("SERIAL", profile.SerialNumber);
        Assert.Equal("ASSET-42", profile.AssetTag);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", profile.SystemUuid);
        Assert.Equal("x64", profile.Architecture);
        Assert.False(profile.IsVirtualMachine);
        Assert.True(profile.IsOnBattery);
        Assert.True(profile.IsTpmPresent);
        Assert.Equal("UEFI\\RES_{FIRMWARE}", profile.SystemFirmwareHardwareId);
        Assert.Equal("HP | Unknown | EliteBook | x64", profile.DisplayLabel);

        PnpDeviceInfo device = Assert.Single(profile.PnpDevices);
        Assert.Equal("Device", device.Name);
        Assert.Equal("PCI\\VEN_1234", device.DeviceId);
        Assert.Equal(["PCI\\VEN_1234"], device.HardwareIds);
    }

    [Fact]
    public async Task GetCurrentAsync_WhenInspectionDataIsUnavailable_UsesFallbackProfile()
    {
        var service = CreateService(_ => Task.FromException<HardwareSnapshot>(new InvalidDataException()));

        HardwareProfile profile = await service.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WindowsFirmwareType.Unknown, profile.FirmwareType);
        Assert.Equal("Unknown", profile.Manufacturer);
        Assert.Equal("Unknown", profile.Model);
        Assert.Equal("Unknown", profile.Product);
        Assert.Equal("Unknown", profile.SerialNumber);
        Assert.Equal("Unknown", profile.AssetTag);
        Assert.Equal("Unknown", profile.SystemUuid);
        Assert.False(profile.IsVirtualMachine);
        Assert.Empty(profile.PnpDevices);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ApplicationException))]
    public async Task GetCurrentAsync_WhenInspectionFailsUnexpectedly_PropagatesFailure(Type exceptionType)
    {
        Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;
        var service = CreateService(_ => Task.FromException<HardwareSnapshot>(exception));

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => service.GetCurrentAsync(TestContext.Current.CancellationToken));

        Assert.IsType(exceptionType, thrown);
    }

    private static HardwareProfileService CreateService(
        Func<CancellationToken, Task<HardwareSnapshot>> getCurrent)
    {
        return new HardwareProfileService(
            new StubHardwareInspector(getCurrent),
            NullLogger<HardwareProfileService>.Instance);
    }

    private sealed class StubHardwareInspector(
        Func<CancellationToken, Task<HardwareSnapshot>> getCurrent) : IHardwareInspector
    {
        public Task<HardwareSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => getCurrent(cancellationToken);
    }
}
