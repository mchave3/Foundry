// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Connect.Models.Configuration;
using Foundry.Connect.Services.Configuration;
using Foundry.Connect.Services.Network;
using Foundry.Utilities.Networking;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Connect.Tests;

public sealed class NetworkBootstrapServiceTests
{
    [Theory]
    [InlineData("provision")]
    [InlineData("configured")]
    [InlineData("discovered")]
    public async Task NetworkOperation_WhenCommandTimesOut_StopsAndReturnsFailure(string operation)
    {
        var configuration = new FoundryConnectConfiguration
        {
            Capabilities = new NetworkCapabilitiesOptions { WifiProvisioned = true },
            Wifi = new WifiSettings
            {
                IsEnabled = true,
                Ssid = "Foundry",
                SecurityType = "Open"
            }
        };
        int commands = 0;
        var service = new NetworkBootstrapService(configuration, new FakeConnectConfigurationService(configuration),
            new CapturingNetworkProfileRoamingService(), NullLogger<NetworkBootstrapService>.Instance,
            getWifiInterfaceIds: static () => [Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")],
            executeNetsh: (_, _) =>
            {
                commands++;
                throw new TimeoutException("Owned command deadline expired.");
            });

        NetworkBootstrapResult result = await (operation switch
        {
            "provision" => service.ApplyProvisionedSettingsAsync(TestContext.Current.CancellationToken),
            "configured" => service.ConnectConfiguredWifiAsync(TestContext.Current.CancellationToken),
            _ => service.ConnectWifiNetworkAsync("Foundry", null, "Open", null, TestContext.Current.CancellationToken)
        });

        Assert.Equal("timeout", Assert.Single(result.HandledFailures).Reason);
        Assert.Equal(1, commands);
    }

    [Fact]
    public void SelectEthernetInterfaceName_UsesFirstNamedEthernetRegardlessOfStatus()
    {
        NetworkAdapterSnapshot[] adapters =
        [
            CreateAdapter("Wi-Fi", NetworkInterfaceType.Wireless80211, OperationalStatus.Up),
            CreateAdapter("", NetworkInterfaceType.Ethernet, OperationalStatus.Up),
            CreateAdapter("Wired 1", NetworkInterfaceType.Ethernet, OperationalStatus.Down),
            CreateAdapter("Wired 2", NetworkInterfaceType.GigabitEthernet, OperationalStatus.Up)
        ];

        string? interfaceName = NetworkBootstrapService.SelectEthernetInterfaceName(adapters);

        Assert.Equal("Wired 1", interfaceName);
    }

    [Fact]
    public async Task ApplyProvisionedSettingsAsync_WhenProvisionedWifiHasNoWinPeAdapter_StillCapturesProfileForRoaming()
    {
        var configuration = new FoundryConnectConfiguration
        {
            Capabilities = new NetworkCapabilitiesOptions
            {
                WifiProvisioned = true
            },
            Wifi = new WifiSettings
            {
                IsEnabled = true,
                Ssid = "Foundry",
                SecurityType = "Open"
            }
        };
        var roamingService = new CapturingNetworkProfileRoamingService();
        var service = new NetworkBootstrapService(
            configuration,
            new FakeConnectConfigurationService(configuration),
            roamingService,
            NullLogger<NetworkBootstrapService>.Instance,
            getWifiInterfaceIds: static () => []);

        NetworkBootstrapResult result = await service.ApplyProvisionedSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Contains("No wireless adapter is available", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        NetworkBootstrapHandledFailure failure = Assert.Single(result.HandledFailures);
        Assert.Equal("network", failure.Kind);
        Assert.Equal("missing_adapter", failure.Reason);
        Assert.Equal("no_wireless_adapter", failure.Code);
        Assert.NotNull(roamingService.WifiCaptureRequest);
        Assert.Equal(NetworkProfileRoamingProfileSource.ProvisionedWifi, roamingService.WifiCaptureRequest.Source);
        Assert.True(roamingService.ProfileExistedDuringCapture);
    }

    [Fact]
    public async Task ApplyProvisionedSettingsAsync_WhenCancelledBeforeNativeCommand_ThrowsCancellation()
    {
        using var tempDirectory = new TemporaryDirectory();
        string profilePath = tempDirectory.CreateFile("wired.xml", "<LANProfile />");
        var configuration = new FoundryConnectConfiguration
        {
            Dot1x = new Dot1xSettings
            {
                IsEnabled = true,
                ProfileTemplatePath = profilePath
            }
        };
        var roamingService = new CapturingNetworkProfileRoamingService();
        var service = new NetworkBootstrapService(
            configuration,
            new FakeConnectConfigurationService(configuration),
            roamingService,
            NullLogger<NetworkBootstrapService>.Instance,
            getWifiInterfaceIds: static () => []);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ApplyProvisionedSettingsAsync(cancellationTokenSource.Token));
        Assert.Null(roamingService.WiredCaptureRequest);
    }

    [Fact]
    public async Task ApplyProvisionedSettingsAsync_WhenWiredProfileTemplateIsMissing_ReturnsStructuredWiredFailure()
    {
        var configuration = new FoundryConnectConfiguration
        {
            Dot1x = new Dot1xSettings
            {
                IsEnabled = true,
                ProfileTemplatePath = "missing-wired-profile.xml"
            }
        };
        var service = new NetworkBootstrapService(
            configuration,
            new FakeConnectConfigurationService(configuration),
            new CapturingNetworkProfileRoamingService(),
            NullLogger<NetworkBootstrapService>.Instance,
            getWifiInterfaceIds: static () => []);

        NetworkBootstrapResult result = await service.ApplyProvisionedSettingsAsync(TestContext.Current.CancellationToken);

        NetworkBootstrapHandledFailure failure = Assert.Single(result.HandledFailures);
        Assert.Equal("network", failure.Kind);
        Assert.Equal("profile_unavailable", failure.Reason);
        Assert.Equal("wired_profile_template_missing", failure.Code);
        Assert.DoesNotContain("wifi_", failure.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyProvisionedSettingsAsync_WhenWiredAndWifiBootstrapFailuresMix_ReturnsSeparateStructuredFailures()
    {
        var configuration = new FoundryConnectConfiguration
        {
            Dot1x = new Dot1xSettings
            {
                IsEnabled = true,
                ProfileTemplatePath = "missing-wired-profile.xml"
            },
            Capabilities = new NetworkCapabilitiesOptions
            {
                WifiProvisioned = true
            },
            Wifi = new WifiSettings
            {
                IsEnabled = true,
                Ssid = "Foundry",
                SecurityType = "Open"
            }
        };
        var service = new NetworkBootstrapService(
            configuration,
            new FakeConnectConfigurationService(configuration),
            new CapturingNetworkProfileRoamingService(),
            NullLogger<NetworkBootstrapService>.Instance,
            getWifiInterfaceIds: static () => []);

        NetworkBootstrapResult result = await service.ApplyProvisionedSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.HandledFailures.Count);
        Assert.Contains(result.HandledFailures, failure => failure.Code == "wired_profile_template_missing");
        Assert.Contains(result.HandledFailures, failure => failure.Code == "no_wireless_adapter");
        Assert.DoesNotContain(result.HandledFailures, failure => failure.Code?.StartsWith("wifi_", StringComparison.OrdinalIgnoreCase) == true && failure.Code == "wired_profile_template_missing");
    }

    private sealed class CapturingNetworkProfileRoamingService : INetworkProfileRoamingService
    {
        public NetworkProfileRoamingCaptureRequest? WifiCaptureRequest { get; private set; }

        public NetworkProfileRoamingCaptureRequest? WiredCaptureRequest { get; private set; }

        public bool ProfileExistedDuringCapture { get; private set; }

        public Task CaptureWifiProfileAsync(NetworkProfileRoamingCaptureRequest request, CancellationToken cancellationToken)
        {
            WifiCaptureRequest = request;
            ProfileExistedDuringCapture = File.Exists(request.ProfilePath);
            return Task.CompletedTask;
        }

        public Task CaptureWiredDot1xProfileAsync(NetworkProfileRoamingCaptureRequest request, CancellationToken cancellationToken)
        {
            WiredCaptureRequest = request;
            return Task.CompletedTask;
        }
    }

    private static NetworkAdapterSnapshot CreateAdapter(
        string name,
        NetworkInterfaceType interfaceType,
        OperationalStatus operationalStatus)
    {
        return new NetworkAdapterSnapshot(
            name,
            name,
            interfaceType,
            operationalStatus,
            string.Empty,
            [],
            [],
            [],
            false);
    }

    private sealed class FakeConnectConfigurationService(FoundryConnectConfiguration configuration) : IConnectConfigurationService
    {
        public string? ConfigurationPath => null;

        public bool IsLoadedFromDisk => false;

        public bool IsBootMediaUpdateRecommended => false;

        public FoundryConnectConfiguration Load() => configuration;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Foundry.Connect.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string fileName, string contents)
        {
            string path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
