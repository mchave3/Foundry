// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Foundry.Connect.Models.Configuration;
using Foundry.Connect.Services.Configuration;
using Foundry.Connect.Services.Runtime;
using Foundry.Connect.Services.System;
using Foundry.Core.Services.Configuration;
using Foundry.Utilities.Networking;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging;

namespace Foundry.Connect.Services.Network;

/// <summary>
/// Applies provisioned 802.1X/Wi-Fi profiles and issues netsh commands for runtime Wi-Fi operations.
/// </summary>
public sealed class NetworkBootstrapService : INetworkBootstrapService
{
    private static readonly TimeSpan WifiConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WifiConnectionPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WifiProfileImportRetryDelay = TimeSpan.FromSeconds(2);
    private const int WinPeWifiProfileImportRetryCount = 3;

    private readonly FoundryConnectConfiguration _configuration;
    private readonly IConnectConfigurationService _configurationService;
    private readonly INetworkProfileRoamingService _networkProfileRoamingService;
    private readonly ILogger<NetworkBootstrapService> _logger;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessExecutionResult>> _executeNetsh;
    private readonly Func<IReadOnlyList<Guid>> _getWifiInterfaceIds;
    private readonly INetworkAdapterSnapshotProvider _networkAdapterSnapshotProvider;

    /// <summary>
    /// Initializes a network bootstrap service.
    /// </summary>
    /// <param name="configuration">The loaded runtime configuration.</param>
    /// <param name="configurationService">The configuration service used to resolve relative asset paths.</param>
    /// <param name="networkProfileRoamingService">The service used to capture eligible profiles for Windows import.</param>
    /// <param name="logger">The logger used for network command diagnostics.</param>
    /// <param name="networkAdapterSnapshotProvider">The provider used to enumerate wired adapters.</param>
    public NetworkBootstrapService(
        FoundryConnectConfiguration configuration,
        IConnectConfigurationService configurationService,
        INetworkProfileRoamingService networkProfileRoamingService,
        ILogger<NetworkBootstrapService> logger,
        INetworkAdapterSnapshotProvider? networkAdapterSnapshotProvider = null)
        : this(
            configuration,
            configurationService,
            networkProfileRoamingService,
            logger,
            NativeWifiApi.GetInterfaceIds,
            networkAdapterSnapshotProvider)
    {
    }

    internal NetworkBootstrapService(
        FoundryConnectConfiguration configuration,
        IConnectConfigurationService configurationService,
        INetworkProfileRoamingService networkProfileRoamingService,
        ILogger<NetworkBootstrapService> logger,
        Func<IReadOnlyList<Guid>> getWifiInterfaceIds,
        INetworkAdapterSnapshotProvider? networkAdapterSnapshotProvider = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessExecutionResult>>? executeNetsh = null)
    {
        _configuration = configuration;
        _configurationService = configurationService;
        _networkProfileRoamingService = networkProfileRoamingService;
        _logger = logger;
        var processExecutor = new ConnectProcessExecutor(logger);
        _executeNetsh = executeNetsh ?? ((arguments, cancellationToken) =>
            processExecutor.ExecuteAsync("netsh", arguments, cancellationToken, TimeSpan.FromSeconds(30)));
        _getWifiInterfaceIds = getWifiInterfaceIds;
        _networkAdapterSnapshotProvider = networkAdapterSnapshotProvider ?? new WindowsNetworkAdapterSnapshotProvider();
    }

    /// <inheritdoc />
    public Task<NetworkBootstrapResult> ApplyProvisionedSettingsAsync(CancellationToken cancellationToken) =>
        HandleCommandTimeoutAsync(() => ApplyProvisionedSettingsCoreAsync(cancellationToken));

    /// <inheritdoc />
    public Task<NetworkBootstrapResult> ConnectConfiguredWifiAsync(CancellationToken cancellationToken) =>
        HandleCommandTimeoutAsync(() => ConnectConfiguredWifiCoreAsync(cancellationToken));

    /// <inheritdoc />
    public Task<NetworkBootstrapResult> ConnectWifiNetworkAsync(string ssid, string? ssidHex, string authentication, string? passphrase, CancellationToken cancellationToken) =>
        HandleCommandTimeoutAsync(() => ConnectWifiNetworkCoreAsync(ssid, ssidHex, authentication, passphrase, cancellationToken));

    /// <inheritdoc />
    public Task<NetworkBootstrapResult> DisconnectWifiAsync(CancellationToken cancellationToken) =>
        HandleCommandTimeoutAsync(() => DisconnectWifiCoreAsync(cancellationToken));

    private async Task<NetworkBootstrapResult> HandleCommandTimeoutAsync(Func<Task<NetworkBootstrapResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Network command timed out; the operation stopped without retrying.");
            return NetworkBootstrapResult.Failed(
                "The network command timed out. Check the current connection before retrying.",
                CreateHandledFailure("timeout", "network_command_timeout"));
        }
    }

    private async Task<NetworkBootstrapResult> ApplyProvisionedSettingsCoreAsync(CancellationToken cancellationToken)
    {
        List<string> messages = [];
        List<NetworkBootstrapHandledFailure> handledFailures = [];
        int requestedActionCount = 0;
        _logger.LogInformation(
            "Provisioned network bootstrap started. WiredDot1xEnabled={WiredDot1xEnabled}, WifiEnabled={WifiEnabled}, WifiProvisioned={WifiProvisioned}",
            _configuration.Dot1x.IsEnabled,
            _configuration.Wifi.IsEnabled,
            _configuration.Capabilities.WifiProvisioned);

        if (_configuration.Dot1x.IsEnabled)
        {
            requestedActionCount++;
            AppendResult(
                messages,
                handledFailures,
                await ApplyWiredDot1xProfileAsync(cancellationToken).ConfigureAwait(false));
        }

        if (_configuration.Capabilities.WifiProvisioned && _configuration.Wifi.IsEnabled)
        {
            requestedActionCount++;
            AppendResult(
                messages,
                handledFailures,
                await EnsureWifiProfileAsync(cancellationToken).ConfigureAwait(false));
        }

        _logger.LogInformation(
            "Provisioned network bootstrap finished. RequestedActionCount={RequestedActionCount}",
            requestedActionCount);

        if (messages.Count == 0)
        {
            return NetworkBootstrapResult.Success("No provisioned network bootstrap actions were requested.");
        }

        return new NetworkBootstrapResult(
            JoinMessages(messages),
            handledFailures);
    }

    private async Task<NetworkBootstrapResult> ConnectConfiguredWifiCoreAsync(CancellationToken cancellationToken)
    {
        List<string> messages = [];
        List<NetworkBootstrapHandledFailure> handledFailures = [];

        if (!_configuration.Capabilities.WifiProvisioned || !_configuration.Wifi.IsEnabled)
        {
            return NetworkBootstrapResult.Failed(
                "Wi-Fi is not provisioned for this image.",
                CreateHandledFailure("profile_unavailable", "wifi_profile_unavailable"));
        }

        AppendResult(
            messages,
            handledFailures,
            await EnsureWifiProfileAsync(cancellationToken).ConfigureAwait(false));
        string? profileName = ResolveWifiProfileName();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            AddHandledFailure(handledFailures, CreateHandledFailure("profile_unavailable", "wifi_profile_unavailable"));
            AddMessage(messages, "No Wi-Fi profile is available to connect.");
            return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
        }

        IReadOnlyList<Guid> wirelessInterfaceIds = _getWifiInterfaceIds();
        if (wirelessInterfaceIds.Count == 0)
        {
            AddHandledFailure(handledFailures, CreateHandledFailure("missing_adapter", "no_wireless_adapter"));
            AddMessage(messages, "No wireless adapter is available to connect the provisioned Wi-Fi profile.");
            return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
        }

        string[] arguments = ["wlan", "connect", $"name={profileName}"];

        ProcessExecutionResult result = await _executeNetsh(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            _logger.LogInformation(
                "Provisioned Wi-Fi connection request failed. ExitCode={ExitCode}, StdOutLength={StdOutLength}, StdErrLength={StdErrLength}",
                result.ExitCode,
                result.StandardOutput.Length,
                result.StandardError.Length);
            AddHandledFailure(handledFailures, CreateHandledFailure("connect_request_failed", "wifi_connect_request_failed"));
            AddMessage(messages, $"Wi-Fi connection request failed: {CollapseError(result)}");
            return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
        }

        string expectedSsid = string.IsNullOrWhiteSpace(_configuration.Wifi.Ssid)
            ? profileName
            : _configuration.Wifi.Ssid.Trim();
        WifiConnectionAttemptResult attemptResult = await WaitForWifiConnectionAsync(
            wirelessInterfaceIds,
            expectedSsid,
            cancellationToken).ConfigureAwait(false);

        if (attemptResult.IsConnected)
        {
            AddMessage(messages, $"Wi-Fi connected to '{expectedSsid}'.");
            return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
        }

        AddHandledFailure(handledFailures, CreateHandledFailure("timeout", "wifi_connect_timeout"));
        AddMessage(messages, $"Wi-Fi connection failed: {attemptResult.FailureMessage}");
        return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
    }

    private async Task<NetworkBootstrapResult> ConnectWifiNetworkCoreAsync(string ssid, string? ssidHex, string authentication, string? passphrase, CancellationToken cancellationToken)
    {
        if (!_configuration.Capabilities.WifiProvisioned && !Debugger.IsAttached)
        {
            return NetworkBootstrapResult.Failed(
                "Wi-Fi support is not provisioned for this image.",
                CreateHandledFailure("profile_unavailable", "wifi_profile_unavailable"));
        }

        string trimmedSsid = ssid?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedSsid))
        {
            return NetworkBootstrapResult.Failed(
                "A discovered Wi-Fi network must provide an SSID before it can be connected.",
                CreateHandledFailure("invalid_input", "wifi_missing_ssid"));
        }

        string securityType = NetworkConfigurationValidator.ResolveDiscoveredWifiSecurityType(authentication);
        if (NetworkConfigurationValidator.IsEnterpriseSecurityType(securityType))
        {
            return NetworkBootstrapResult.Failed(
                "Enterprise Wi-Fi from the discovery list requires a provisioned profile template in this build.",
                CreateHandledFailure("unsupported", "wifi_runtime_not_supported"));
        }

        string? profilePath = null;
        try
        {
            profilePath = await WriteTemporaryWifiProfileAsync(
                WifiProfileXmlBuilder.Build(trimmedSsid, securityType, passphrase, ssidHex),
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<Guid> wirelessInterfaceIds = _getWifiInterfaceIds();
            if (wirelessInterfaceIds.Count == 0)
            {
                return NetworkBootstrapResult.Failed(
                    "No wireless adapter is available to connect the selected Wi-Fi network.",
                    CreateHandledFailure("missing_adapter", "no_wireless_adapter"));
            }

            ProcessExecutionResult addProfileResult = await ImportWifiProfileAsync(profilePath, cancellationToken).ConfigureAwait(false);
            if (addProfileResult.ExitCode != 0)
            {
                _logger.LogInformation(
                    "Failed to import discovered Wi-Fi profile. ExitCode={ExitCode}",
                    addProfileResult.ExitCode);
                return NetworkBootstrapResult.Failed(
                    $"Wi-Fi profile import failed for '{trimmedSsid}': {CollapseError(addProfileResult)}",
                    CreateHandledFailure("profile_import_failed", "wifi_profile_import_failed"));
            }

            ProcessExecutionResult connectResult = await _executeNetsh(
                ["wlan", "connect", $"name={trimmedSsid}"],
                cancellationToken).ConfigureAwait(false);
            if (connectResult.ExitCode != 0)
            {
                return NetworkBootstrapResult.Failed(
                    $"Wi-Fi connection request failed for '{trimmedSsid}': {CollapseError(connectResult)}",
                    CreateHandledFailure("connect_request_failed", "wifi_connect_request_failed"));
            }

            WifiConnectionAttemptResult attemptResult = await WaitForWifiConnectionAsync(
                wirelessInterfaceIds,
                trimmedSsid,
                cancellationToken).ConfigureAwait(false);

            if (!attemptResult.IsConnected)
            {
                return NetworkBootstrapResult.Failed(
                    $"Wi-Fi connection failed for '{trimmedSsid}': {attemptResult.FailureMessage}",
                    CreateHandledFailure("timeout", "wifi_connect_timeout"));
            }

            await _networkProfileRoamingService.CaptureWifiProfileAsync(
                new NetworkProfileRoamingCaptureRequest(
                    profilePath,
                    NetworkProfileRoamingProfileSource.ManualWifi,
                    NetworkProfileRoamingConnectivityExpectation.PreOobeConnectable),
                cancellationToken).ConfigureAwait(false);

            return NetworkBootstrapResult.Success($"Wi-Fi connected to '{trimmedSsid}'.");
        }
        finally
        {
            DeleteTemporaryProfile(profilePath);
        }
    }

    private async Task<NetworkBootstrapResult> DisconnectWifiCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> wirelessInterfaceIds = _getWifiInterfaceIds();
        if (wirelessInterfaceIds.Count == 0)
        {
            return NetworkBootstrapResult.Failed(
                "No wireless adapter is available to disconnect.",
                CreateHandledFailure("missing_adapter", "no_wireless_adapter"));
        }

        string? connectedSsid = NativeWifiApi.GetConnectedSsid();
        if (string.IsNullOrWhiteSpace(connectedSsid))
        {
            return NetworkBootstrapResult.Success("Wi-Fi is already disconnected.");
        }

        ProcessExecutionResult disconnectResult = await _executeNetsh(
            ["wlan", "disconnect"],
            cancellationToken).ConfigureAwait(false);
        if (disconnectResult.ExitCode != 0)
        {
            return NetworkBootstrapResult.Failed(
                $"Wi-Fi disconnect request failed: {CollapseError(disconnectResult)}",
                CreateHandledFailure("disconnect_request_failed", "wifi_disconnect_request_failed"));
        }

        WifiDisconnectAttemptResult attemptResult = await WaitForWifiDisconnectionAsync(
            wirelessInterfaceIds,
            connectedSsid,
            cancellationToken).ConfigureAwait(false);

        return attemptResult.IsDisconnected
            ? NetworkBootstrapResult.Success($"Wi-Fi disconnected from '{connectedSsid}'.")
            : NetworkBootstrapResult.Failed(
                $"Wi-Fi disconnect failed: {attemptResult.FailureMessage}",
                CreateHandledFailure("timeout", "wifi_disconnect_timeout"));
    }

    private async Task<NetworkBootstrapResult> ApplyWiredDot1xProfileAsync(CancellationToken cancellationToken)
    {
        string? profilePath = ProvisionedWifiProfileResolver.ResolveAssetPath(
            _configuration.Dot1x.ProfileTemplatePath,
            _configurationService.ConfigurationPath);
        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
        {
            return NetworkBootstrapResult.Failed(
                "Wired 802.1X is enabled, but no wired profile template was found.",
                CreateHandledFailure("profile_unavailable", "wired_profile_template_missing"));
        }

        List<string> messages = [];
        List<NetworkBootstrapHandledFailure> handledFailures = [];
        string? certificatePath = ProvisionedWifiProfileResolver.ResolveAssetPath(
            _configuration.Dot1x.CertificatePath,
            _configurationService.ConfigurationPath);
        if (!string.IsNullOrWhiteSpace(certificatePath) && File.Exists(certificatePath))
        {
            AppendCertificateImportResult(
                messages,
                handledFailures,
                ImportCertificate(
                    certificatePath,
                    _configuration.Dot1x.CertificatePfxPassword,
                    "wired_certificate_import_failed"));
        }

        if (_configuration.Dot1x.AllowRuntimeCredentials)
        {
            messages.Add("Runtime-entered wired 802.1X credentials are not supported in this build. Use a profile template that already contains the required enterprise settings.");
            AddHandledFailure(handledFailures, CreateHandledFailure("unsupported", "wired_runtime_not_supported"));
        }

        ProcessExecutionResult addProfileResult = await _executeNetsh(
            ["lan", "add", "profile", $"filename={profilePath}"],
            cancellationToken).ConfigureAwait(false);
        if (addProfileResult.ExitCode != 0)
        {
            _logger.LogInformation(
                "Failed to add wired 802.1X profile. ExitCode={ExitCode}, StdOutLength={StdOutLength}, StdErrLength={StdErrLength}",
                addProfileResult.ExitCode,
                addProfileResult.StandardOutput.Length,
                addProfileResult.StandardError.Length);
            messages.Add($"Wired 802.1X profile import failed: {CollapseError(addProfileResult)}");
            AddHandledFailure(handledFailures, CreateHandledFailure("profile_import_failed", "wired_profile_import_failed"));
            return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
        }

        messages.Add("Wired 802.1X profile imported.");
        await _networkProfileRoamingService.CaptureWiredDot1xProfileAsync(
            new NetworkProfileRoamingCaptureRequest(
                profilePath,
                NetworkProfileRoamingProfileSource.ProvisionedWiredDot1x,
                ResolveConnectivityExpectation(_configuration.Dot1x.AuthenticationMode),
                ResolveExistingAssetPaths(certificatePath),
                _configuration.Dot1x.CertificatePfxPasswordSecret),
            cancellationToken).ConfigureAwait(false);

        string? ethernetInterfaceName = GetEthernetInterfaceName();
        if (!string.IsNullOrWhiteSpace(ethernetInterfaceName))
        {
            ProcessExecutionResult reconnectResult = await _executeNetsh(
                ["lan", "reconnect", $"interface={ethernetInterfaceName}"],
                cancellationToken).ConfigureAwait(false);
            if (reconnectResult.ExitCode == 0)
            {
                messages.Add($"Wired reconnect requested on '{ethernetInterfaceName}'.");
            }
            else
            {
                messages.Add($"Wired reconnect request failed: {CollapseError(reconnectResult)}");
                AddHandledFailure(handledFailures, CreateHandledFailure("reconnect_request_failed", "wired_reconnect_request_failed"));
            }
        }

        return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
    }

    private async Task<NetworkBootstrapResult> EnsureWifiProfileAsync(CancellationToken cancellationToken)
    {
        List<string> messages = [];
        List<NetworkBootstrapHandledFailure> handledFailures = [];

        string? certificatePath = ProvisionedWifiProfileResolver.ResolveAssetPath(
            _configuration.Wifi.CertificatePath,
            _configurationService.ConfigurationPath);
        if (!string.IsNullOrWhiteSpace(certificatePath) && File.Exists(certificatePath))
        {
            AppendCertificateImportResult(
                messages,
                handledFailures,
                ImportCertificate(
                    certificatePath,
                    _configuration.Wifi.CertificatePfxPassword,
                    "wifi_certificate_import_failed"));
        }

        string? wifiProfilePath = await EnsureWifiProfileFileAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wifiProfilePath))
        {
            messages.Add("No Wi-Fi profile is configured for this image.");
            AddHandledFailure(handledFailures, CreateHandledFailure("profile_unavailable", "wifi_profile_unavailable"));
            return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
        }

        try
        {
            await _networkProfileRoamingService.CaptureWifiProfileAsync(
                new NetworkProfileRoamingCaptureRequest(
                    wifiProfilePath,
                    NetworkProfileRoamingProfileSource.ProvisionedWifi,
                    ResolveWifiConnectivityExpectation(),
                    ResolveExistingAssetPaths(certificatePath),
                    _configuration.Wifi.CertificatePfxPasswordSecret),
                cancellationToken).ConfigureAwait(false);

            if (_getWifiInterfaceIds().Count == 0)
            {
                messages.Add("No wireless adapter is available to import the provisioned Wi-Fi profile in WinPE.");
                AddHandledFailure(handledFailures, CreateHandledFailure("missing_adapter", "no_wireless_adapter"));
                return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
            }

            ProcessExecutionResult addProfileResult = await ImportWifiProfileAsync(wifiProfilePath, cancellationToken).ConfigureAwait(false);

            if (addProfileResult.ExitCode != 0)
            {
                _logger.LogInformation(
                    "Failed to add provisioned Wi-Fi profile. ExitCode={ExitCode}",
                    addProfileResult.ExitCode);
                messages.Add($"Wi-Fi profile import failed: {CollapseError(addProfileResult)}");
                AddHandledFailure(handledFailures, CreateHandledFailure("profile_import_failed", "wifi_profile_import_failed"));
            }
            else
            {
                messages.Add("Wi-Fi profile imported.");
            }
        }
        finally
        {
            if (!_configuration.Wifi.HasEnterpriseProfile)
            {
                DeleteTemporaryProfile(wifiProfilePath);
            }
        }

        if (_configuration.Wifi.HasEnterpriseProfile && _configuration.Wifi.AllowRuntimeCredentials)
        {
            messages.Add("Runtime-entered Wi-Fi 802.1X credentials are not supported in this build. Use a provisioned enterprise profile template.");
            AddHandledFailure(handledFailures, CreateHandledFailure("unsupported", "wifi_runtime_not_supported"));
        }

        return new NetworkBootstrapResult(JoinMessages(messages), handledFailures);
    }

    private async Task<string?> EnsureWifiProfileFileAsync(CancellationToken cancellationToken)
    {
        if (_configuration.Wifi.HasEnterpriseProfile)
        {
            string? enterpriseProfilePath = ProvisionedWifiProfileResolver.ResolveAssetPath(
                _configuration.Wifi.EnterpriseProfileTemplatePath,
                _configurationService.ConfigurationPath);
            return !string.IsNullOrWhiteSpace(enterpriseProfilePath) && File.Exists(enterpriseProfilePath)
                ? enterpriseProfilePath
                : null;
        }

        if (string.IsNullOrWhiteSpace(_configuration.Wifi.Ssid) || string.IsNullOrWhiteSpace(_configuration.Wifi.SecurityType))
        {
            return null;
        }

        return await WriteTemporaryWifiProfileAsync(
            WifiProfileXmlBuilder.Build(_configuration.Wifi.Ssid.Trim(), _configuration.Wifi.SecurityType.Trim(), _configuration.Wifi.Passphrase),
            cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveWifiProfileName()
    {
        return ProvisionedWifiProfileResolver.ResolveProfileName(
            _configuration.Wifi,
            _configurationService.ConfigurationPath);
    }

    private async Task<ProcessExecutionResult> ImportWifiProfileAsync(
        string profilePath,
        CancellationToken cancellationToken)
    {
        int maxAttempts = ConnectWorkspacePaths.IsWinPeRuntime()
            ? WinPeWifiProfileImportRetryCount
            : 1;

        ProcessExecutionResult? lastResult = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lastResult = await _executeNetsh(
                ["wlan", "add", "profile", $"filename={profilePath}"],
                cancellationToken).ConfigureAwait(false);

            if (lastResult.ExitCode == 0)
            {
                return lastResult;
            }

            if (attempt >= maxAttempts)
            {
                break;
            }

            // WinPE can expose WLAN AutoConfig before profile import is fully ready; a short retry avoids false failures.
            _logger.LogInformation(
                "Wi-Fi profile import attempt {Attempt} failed in WinPE. Retrying in {DelaySeconds}s. ExitCode={ExitCode}",
                attempt,
                WifiProfileImportRetryDelay.TotalSeconds,
                lastResult.ExitCode);

            await Task.Delay(WifiProfileImportRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return lastResult!;
    }

    private async Task<string> WriteTemporaryWifiProfileAsync(
        string profileXml,
        CancellationToken cancellationToken)
    {
        string profilePath = Path.Combine(ResolveTemporaryProfileRoot(), $"wifi-profile-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(
            profilePath,
            profileXml,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return profilePath;
    }

    private void DeleteTemporaryProfile(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath))
        {
            return;
        }

        try
        {
            if (File.Exists(profilePath))
            {
                File.Delete(profilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete a temporary Wi-Fi profile file.");
        }
    }

    private string ResolveTemporaryProfileRoot()
    {
        foreach (string candidateDirectory in ConnectWorkspacePaths.EnumerateTemporaryDirectories("Foundry.Connect"))
        {
            if (string.IsNullOrWhiteSpace(candidateDirectory))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(candidateDirectory);
                return candidateDirectory;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to create temporary Wi-Fi profile directory at {CandidateDirectory}.", candidateDirectory);
            }
        }

        throw new InvalidOperationException("No writable temporary directory is available for Wi-Fi profile generation.");
    }

    private string? GetEthernetInterfaceName()
    {
        return SelectEthernetInterfaceName(_networkAdapterSnapshotProvider.GetAdapters());
    }

    internal static string? SelectEthernetInterfaceName(
        IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        return adapters
            .Where(static adapter => adapter.InterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.Ethernet3Megabit)
            .Select(static adapter => adapter.Name)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private NetworkProfileRoamingConnectivityExpectation ResolveWifiConnectivityExpectation()
    {
        if (!_configuration.Wifi.HasEnterpriseProfile)
        {
            return NetworkProfileRoamingConnectivityExpectation.PreOobeConnectable;
        }

        return ResolveConnectivityExpectation(_configuration.Wifi.EnterpriseAuthenticationMode);
    }

    private static NetworkProfileRoamingConnectivityExpectation ResolveConnectivityExpectation(NetworkAuthenticationMode authenticationMode)
    {
        return authenticationMode switch
        {
            NetworkAuthenticationMode.MachineOnly => NetworkProfileRoamingConnectivityExpectation.DependsOnMachineCredential,
            _ => NetworkProfileRoamingConnectivityExpectation.ImportOnly
        };
    }

    private static IReadOnlyList<string> ResolveExistingAssetPaths(params string?[] assetPaths)
    {
        return assetPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(static path => path!)
            .ToArray();
    }

    private CertificateImportResult ImportCertificate(string certificatePath, string? certificatePfxPassword, string failureCode)
    {
        try
        {
            if (IsPfxPath(certificatePath))
            {
                X509Certificate2 pfxCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                    certificatePath,
                    certificatePfxPassword,
                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                using X509Store myStore = new(StoreName.My, StoreLocation.LocalMachine);
                myStore.Open(OpenFlags.ReadWrite);
                AddCertificateIfMissing(myStore, pfxCertificate);
                return new CertificateImportResult($"Certificate '{Path.GetFileName(certificatePath)}' was imported into the local machine personal store.");
            }

            X509Certificate2 publicCertificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            using X509Store rootStore = new(StoreName.Root, StoreLocation.LocalMachine);
            rootStore.Open(OpenFlags.ReadWrite);
            bool alreadyPresent = rootStore.Certificates
                .Find(X509FindType.FindByThumbprint, publicCertificate.Thumbprint, validOnly: false)
                .Count > 0;
            if (!alreadyPresent)
            {
                rootStore.Add(publicCertificate);
            }

            return alreadyPresent
                ? new CertificateImportResult($"Certificate '{Path.GetFileName(certificatePath)}' was already trusted.")
                : new CertificateImportResult($"Certificate '{Path.GetFileName(certificatePath)}' was imported into the local machine root store.");
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                ex,
                "Failed to import certificate. CertificateFileName={CertificateFileName}",
                Path.GetFileName(certificatePath));
            return new CertificateImportResult(
                $"Certificate import failed for '{Path.GetFileName(certificatePath)}': {ex.Message}",
                CreateHandledFailure("certificate_import_failed", failureCode));
        }
    }

    private static void AppendResult(
        List<string> messages,
        List<NetworkBootstrapHandledFailure> handledFailures,
        NetworkBootstrapResult result)
    {
        AddMessage(messages, result.StatusMessage);
        foreach (NetworkBootstrapHandledFailure failure in result.HandledFailures)
        {
            AddHandledFailure(handledFailures, failure);
        }
    }

    private static void AppendCertificateImportResult(
        List<string> messages,
        List<NetworkBootstrapHandledFailure> handledFailures,
        CertificateImportResult result)
    {
        AddMessage(messages, result.Message);
        if (result.Failure is not null)
        {
            AddHandledFailure(handledFailures, result.Failure);
        }
    }

    private static void AddHandledFailure(
        List<NetworkBootstrapHandledFailure> handledFailures,
        NetworkBootstrapHandledFailure failure)
    {
        if (!handledFailures.Contains(failure))
        {
            handledFailures.Add(failure);
        }
    }

    private static void AddMessage(List<string> messages, string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            messages.Add(message);
        }
    }

    private static string JoinMessages(IEnumerable<string> messages)
    {
        return string.Join(" ", messages.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static NetworkBootstrapHandledFailure CreateHandledFailure(string reason, string code)
    {
        return new NetworkBootstrapHandledFailure("network", reason, code);
    }

    private static void AddCertificateIfMissing(X509Store store, X509Certificate2 certificate)
    {
        bool alreadyPresent = store.Certificates
            .Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
            .Count > 0;
        if (!alreadyPresent)
        {
            store.Add(certificate);
        }
    }

    private static bool IsPfxPath(string certificatePath)
    {
        string extension = Path.GetExtension(certificatePath);
        return string.Equals(extension, ".pfx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".p12", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConnectionInProgress(NativeWifiApi.WlanInterfaceState state)
    {
        return state is NativeWifiApi.WlanInterfaceState.Associating
            or NativeWifiApi.WlanInterfaceState.Authenticating
            or NativeWifiApi.WlanInterfaceState.Discovering
            or NativeWifiApi.WlanInterfaceState.Disconnecting;
    }

    private static async Task<WifiConnectionAttemptResult> WaitForWifiConnectionAsync(
        IReadOnlyList<Guid> interfaceIds,
        string expectedSsid,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + WifiConnectionTimeout;
        bool sawConnectionTransition = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (Guid interfaceId in interfaceIds)
            {
                NativeWifiApi.WifiInterfaceConnectionInfo? connectionInfo = NativeWifiApi.GetInterfaceConnectionInfo(interfaceId);
                if (connectionInfo is null)
                {
                    continue;
                }

                if (connectionInfo.State == NativeWifiApi.WlanInterfaceState.Connected &&
                    string.Equals(connectionInfo.CurrentSsid, expectedSsid, StringComparison.Ordinal))
                {
                    return WifiConnectionAttemptResult.Success();
                }

                if (IsConnectionInProgress(connectionInfo.State))
                {
                    sawConnectionTransition = true;
                }
            }

            await Task.Delay(WifiConnectionPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return sawConnectionTransition
            ? WifiConnectionAttemptResult.Failure($"Windows started the Wi-Fi connection workflow, but '{expectedSsid}' did not reach the connected state within {WifiConnectionTimeout.TotalSeconds:0} seconds.")
            : WifiConnectionAttemptResult.Failure($"Windows accepted the request, but the wireless interface never transitioned into an active connection attempt.");
    }

    private static async Task<WifiDisconnectAttemptResult> WaitForWifiDisconnectionAsync(
        IReadOnlyList<Guid> interfaceIds,
        string disconnectedSsid,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + WifiConnectionTimeout;
        bool sawDisconnectTransition = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isStillConnectedToTarget = false;

            foreach (Guid interfaceId in interfaceIds)
            {
                NativeWifiApi.WifiInterfaceConnectionInfo? connectionInfo = NativeWifiApi.GetInterfaceConnectionInfo(interfaceId);
                if (connectionInfo is null)
                {
                    continue;
                }

                if (connectionInfo.State == NativeWifiApi.WlanInterfaceState.Disconnecting)
                {
                    sawDisconnectTransition = true;
                }

                if (connectionInfo.State == NativeWifiApi.WlanInterfaceState.Connected &&
                    string.Equals(connectionInfo.CurrentSsid, disconnectedSsid, StringComparison.Ordinal))
                {
                    isStillConnectedToTarget = true;
                }
            }

            if (!isStillConnectedToTarget)
            {
                return WifiDisconnectAttemptResult.Success();
            }

            await Task.Delay(WifiConnectionPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return sawDisconnectTransition
            ? WifiDisconnectAttemptResult.Failure($"Windows started the Wi-Fi disconnect workflow, but '{disconnectedSsid}' remained connected after {WifiConnectionTimeout.TotalSeconds:0} seconds.")
            : WifiDisconnectAttemptResult.Failure($"Windows accepted the request, but '{disconnectedSsid}' did not transition away from the connected state.");
    }

    private static string CollapseError(ProcessExecutionResult result)
    {
        string message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = $"Exit code {result.ExitCode}.";
        }

        return message.Replace(Environment.NewLine, " ").Trim();
    }

    private sealed record WifiConnectionAttemptResult(bool IsConnected, string? FailureMessage)
    {
        public static WifiConnectionAttemptResult Success()
        {
            return new WifiConnectionAttemptResult(true, null);
        }

        public static WifiConnectionAttemptResult Failure(string message)
        {
            return new WifiConnectionAttemptResult(false, message);
        }
    }

    private sealed record WifiDisconnectAttemptResult(bool IsDisconnected, string? FailureMessage)
    {
        public static WifiDisconnectAttemptResult Success()
        {
            return new WifiDisconnectAttemptResult(true, null);
        }

        public static WifiDisconnectAttemptResult Failure(string message)
        {
            return new WifiDisconnectAttemptResult(false, message);
        }
    }

    private sealed record CertificateImportResult(string Message, NetworkBootstrapHandledFailure? Failure = null);

}
