// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Services.Configuration;

/// <summary>
/// Identifies one user-facing configuration area summarized on the media Start page.
/// </summary>
public enum ConfigurationOverviewItem
{
    Architecture,
    SecureBoot,
    WinPeLanguage,
    TimeZone,
    DeploymentCompletion,
    DeploymentProtection,
    DriverOptions,
    EthernetDot1x,
    Wifi,
    AutopilotJsonProfile,
    AutopilotZeroTouch,
    AutopilotInteractive,
    OperatingSystemSelection,
    Unattend,
    MachineNaming,
    Oobe,
    OptionalFeatures,
    AppxRemoval,
    AiComponents
}

/// <summary>
/// Describes how a configuration area should be presented in a read-only overview.
/// </summary>
public enum ConfigurationOverviewState
{
    Default,
    Configured,
    Disabled,
    NotConfigured,
    NotSelected,
    NeedsAttention
}

/// <summary>
/// Supplies persisted configuration and runtime-only readiness inputs for overview evaluation.
/// </summary>
public sealed record ConfigurationOverviewContext
{
    /// <summary>
    /// Gets the current persisted Foundry configuration.
    /// </summary>
    public required FoundryConfigurationDocument Configuration { get; init; }

    /// <summary>
    /// Gets the network configuration after applying session-only secrets.
    /// </summary>
    public required NetworkSettings EffectiveNetwork { get; init; }

    /// <summary>
    /// Gets a value indicating whether the selected WinPE language is available.
    /// </summary>
    public bool IsWinPeLanguageReady { get; init; }

    /// <summary>
    /// Gets a value indicating whether configured custom drivers can be loaded.
    /// </summary>
    public bool IsCustomDriverConfigurationReady { get; init; }

    /// <summary>
    /// Gets a value indicating whether enabled deployment protection has a valid session secret.
    /// </summary>
    public bool IsDeploymentProtectionSecretReady { get; init; }

    /// <summary>
    /// Gets a value indicating whether OOBE local account secrets are valid for the current persisted configuration.
    /// </summary>
    public bool IsOobeAccountConfigurationReady { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the active Autopilot provisioning mode is valid.
    /// </summary>
    public bool IsAutopilotConfigurationReady { get; init; }

    /// <summary>
    /// Gets whether enabled answer-file sources, references, and media protection are ready.
    /// </summary>
    public bool IsUnattendConfigurationReady { get; init; } = true;
}

/// <summary>
/// Contains the evaluated state of every Start-page configuration overview item.
/// </summary>
public sealed class ConfigurationOverviewEvaluation
{
    private readonly IReadOnlyDictionary<ConfigurationOverviewItem, ConfigurationOverviewState> states;

    internal ConfigurationOverviewEvaluation(
        IReadOnlyDictionary<ConfigurationOverviewItem, ConfigurationOverviewState> states)
    {
        this.states = states;
    }

    /// <summary>
    /// Gets the evaluated state for the requested overview item.
    /// </summary>
    /// <param name="item">Overview item to query.</param>
    /// <returns>The current user-facing configuration state.</returns>
    public ConfigurationOverviewState this[ConfigurationOverviewItem item] => states[item];

    /// <summary>
    /// Gets the number of overview items that require user action.
    /// </summary>
    public int NeedsAttentionCount => states.Values.Count(value => value == ConfigurationOverviewState.NeedsAttention);
}

/// <summary>
/// Evaluates page-level configuration state independently from WinUI presentation concerns.
/// </summary>
public static class ConfigurationOverviewEvaluator
{
    /// <summary>
    /// Evaluates the complete Start-page configuration overview.
    /// </summary>
    /// <param name="context">Persisted configuration and runtime readiness inputs.</param>
    /// <returns>An evaluation containing one state for every overview item.</returns>
    public static ConfigurationOverviewEvaluation Evaluate(ConfigurationOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        FoundryConfigurationDocument configuration = context.Configuration;
        GeneralSettings general = configuration.General;
        CustomizationSettings customization = configuration.Customization;

        var states = new Dictionary<ConfigurationOverviewItem, ConfigurationOverviewState>
        {
            [ConfigurationOverviewItem.Unattend] = !configuration.Unattend.IsEnabled
                ? ConfigurationOverviewState.Disabled
                : context.IsUnattendConfigurationReady
                    ? ConfigurationOverviewState.Configured
                    : ConfigurationOverviewState.NeedsAttention,
            [ConfigurationOverviewItem.Architecture] = general.Architecture == WinPeArchitecture.X64
                ? ConfigurationOverviewState.Default
                : ConfigurationOverviewState.Configured,
            [ConfigurationOverviewItem.SecureBoot] = general.UseCa2023
                ? ConfigurationOverviewState.Default
                : ConfigurationOverviewState.Configured,
            [ConfigurationOverviewItem.WinPeLanguage] = context.IsWinPeLanguageReady
                ? ConfigurationOverviewState.Configured
                : ConfigurationOverviewState.NeedsAttention,
            [ConfigurationOverviewItem.TimeZone] = string.IsNullOrWhiteSpace(configuration.Localization.DefaultTimeZoneId)
                ? ConfigurationOverviewState.Default
                : ConfigurationOverviewState.Configured,
            [ConfigurationOverviewItem.DeploymentCompletion] = IsDefaultCompletion(general)
                ? ConfigurationOverviewState.Default
                : ConfigurationOverviewState.Configured,
            [ConfigurationOverviewItem.DeploymentProtection] = EvaluateDeploymentProtection(
                general.DeploymentProtection.IsEnabled,
                context.IsDeploymentProtectionSecretReady,
                OobeAccountConfigurationValidator.RequiresProtectedMedia(customization.Oobe)),
            [ConfigurationOverviewItem.DriverOptions] = EvaluateDrivers(general, context.IsCustomDriverConfigurationReady),
            [ConfigurationOverviewItem.EthernetDot1x] = EvaluateNetworkTransport(
                configuration.Network.Dot1x.IsEnabled,
                CreateEthernetOnlyNetwork(context.EffectiveNetwork)),
            [ConfigurationOverviewItem.Wifi] = EvaluateNetworkTransport(
                configuration.Network.WifiProvisioned,
                CreateWifiOnlyNetwork(context.EffectiveNetwork)),
            [ConfigurationOverviewItem.OperatingSystemSelection] = EvaluateOptionalFeature(
                configuration.OperatingSystemSelection.IsEnabled),
            [ConfigurationOverviewItem.MachineNaming] = EvaluateMachineNaming(customization.MachineNaming),
            [ConfigurationOverviewItem.Oobe] = EvaluateOobe(
                customization.Oobe,
                configuration.Autopilot,
                context.IsOobeAccountConfigurationReady,
                general.DeploymentProtection.IsEnabled),
            [ConfigurationOverviewItem.OptionalFeatures] = EvaluateOptionalFeature(
                customization.WindowsOptionalFeatures.IsEnabled &&
                (customization.WindowsOptionalFeatures.EnabledFeatureIds.Count > 0 ||
                 customization.WindowsOptionalFeatures.DisabledFeatureIds.Count > 0)),
            [ConfigurationOverviewItem.AppxRemoval] = EvaluateOptionalFeature(
                customization.AppxRemoval.IsEnabled && customization.AppxRemoval.PackageNames.Count > 0),
            [ConfigurationOverviewItem.AiComponents] = EvaluateOptionalFeature(
                customization.AiComponentRemoval.IsEnabled && customization.AiComponentRemoval.HasAnyAction())
        };

        AddAutopilotStates(states, configuration.Autopilot, context.IsAutopilotConfigurationReady);
        return new ConfigurationOverviewEvaluation(states);
    }

    private static bool IsDefaultCompletion(GeneralSettings settings) =>
        settings.AutomaticRebootEnabled &&
        DeploymentRebootDelay.NormalizeRuntime(settings.AutomaticRebootDelaySeconds) == DeploymentRebootDelay.DefaultSeconds;

    private static ConfigurationOverviewState EvaluateDrivers(
        GeneralSettings settings,
        bool isCustomDriverConfigurationReady)
    {
        bool isConfigured = settings.IncludeDellDrivers ||
            settings.IncludeHpDrivers ||
            !string.IsNullOrWhiteSpace(settings.CustomDriverDirectoryPath);
        return isConfigured
            ? isCustomDriverConfigurationReady
                ? ConfigurationOverviewState.Configured
                : ConfigurationOverviewState.NeedsAttention
            : ConfigurationOverviewState.Disabled;
    }

    private static ConfigurationOverviewState EvaluateEnabledFeature(bool isEnabled, bool isReady) =>
        !isEnabled
            ? ConfigurationOverviewState.Disabled
            : isReady
                ? ConfigurationOverviewState.Configured
                : ConfigurationOverviewState.NeedsAttention;

    private static ConfigurationOverviewState EvaluateDeploymentProtection(
        bool isEnabled,
        bool isReady,
        bool isRequired) =>
        !isEnabled
            ? isRequired
                ? ConfigurationOverviewState.NeedsAttention
                : ConfigurationOverviewState.Disabled
            : isReady
                ? ConfigurationOverviewState.Configured
                : ConfigurationOverviewState.NeedsAttention;

    private static ConfigurationOverviewState EvaluateOptionalFeature(bool isEnabled) =>
        isEnabled ? ConfigurationOverviewState.Configured : ConfigurationOverviewState.Disabled;

    private static ConfigurationOverviewState EvaluateNetworkTransport(bool isEnabled, NetworkSettings settings) =>
        !isEnabled
            ? ConfigurationOverviewState.NotConfigured
            : NetworkConfigurationValidator.Validate(settings).IsValid
                ? ConfigurationOverviewState.Configured
                : ConfigurationOverviewState.NeedsAttention;

    private static ConfigurationOverviewState EvaluateMachineNaming(MachineNamingSettings settings) =>
        !settings.IsEnabled
            ? ConfigurationOverviewState.Disabled
            : MachineNamingValidator.Validate(settings).IsValid
                ? ConfigurationOverviewState.Configured
                : ConfigurationOverviewState.NeedsAttention;

    private static ConfigurationOverviewState EvaluateOobe(
        OobeSettings settings,
        AutopilotSettings autopilot,
        bool isSecretStateReady,
        bool isDeploymentProtectionEnabled) =>
        !settings.IsEnabled
            ? ConfigurationOverviewState.Disabled
            : OobeAccountConfigurationValidator.Validate(settings).IsValid &&
              isSecretStateReady &&
              (!OobeAccountConfigurationValidator.RequiresProtectedMedia(settings) || isDeploymentProtectionEnabled) &&
              IsOobeCompatibleWithAutopilot(autopilot, settings)
                ? ConfigurationOverviewState.Configured
                : ConfigurationOverviewState.NeedsAttention;

    private static bool IsOobeCompatibleWithAutopilot(AutopilotSettings autopilot, OobeSettings oobe)
    {
        if (!autopilot.IsEnabled)
        {
            return true;
        }

        return oobe.AdditionalAccounts.Count == 0;
    }

    private static NetworkSettings CreateEthernetOnlyNetwork(NetworkSettings settings) => settings with
    {
        WifiProvisioned = false,
        Wifi = new WifiSettings()
    };

    private static NetworkSettings CreateWifiOnlyNetwork(NetworkSettings settings) => settings with
    {
        Dot1x = new Dot1xSettings()
    };

    private static void AddAutopilotStates(
        IDictionary<ConfigurationOverviewItem, ConfigurationOverviewState> states,
        AutopilotSettings settings,
        bool isReady)
    {
        states[ConfigurationOverviewItem.AutopilotJsonProfile] = EvaluateAutopilotMode(
            settings,
            AutopilotProvisioningMode.JsonProfile,
            isReady);
        states[ConfigurationOverviewItem.AutopilotZeroTouch] = EvaluateAutopilotMode(
            settings,
            AutopilotProvisioningMode.HardwareHashUpload,
            isReady);
        states[ConfigurationOverviewItem.AutopilotInteractive] = EvaluateAutopilotMode(
            settings,
            AutopilotProvisioningMode.InteractiveHardwareHashUpload,
            isReady);
    }

    private static ConfigurationOverviewState EvaluateAutopilotMode(
        AutopilotSettings settings,
        AutopilotProvisioningMode mode,
        bool isReady)
    {
        if (!settings.IsEnabled || settings.ProvisioningMode != mode)
        {
            return ConfigurationOverviewState.NotSelected;
        }

        return isReady ? ConfigurationOverviewState.Configured : ConfigurationOverviewState.NeedsAttention;
    }
}
