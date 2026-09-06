// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;

namespace Foundry.Core.Services.Configuration;

public enum ConfigurationNavigationTarget
{
    None,
    Adk,
    General,
    EthernetDot1x,
    Wifi,
    AutopilotJsonProfile,
    AutopilotHardwareHashUpload,
    AutopilotInteractiveHardwareHashUpload,
    OperatingSystemSelection,
    Unattend,
    MachineNaming,
    Oobe,
    WindowsOptionalFeatures,
    AppxRemoval,
    AiComponentRemoval
}

public static class ConfigurationNavigationTargetResolver
{
    public static ConfigurationNavigationTarget ResolveNetwork(NetworkConfigurationValidationCode validationCode) => validationCode switch
    {
        NetworkConfigurationValidationCode.None => ConfigurationNavigationTarget.None,
        NetworkConfigurationValidationCode.WiredProfileTemplateRequired or
        NetworkConfigurationValidationCode.WiredProfileTemplateMissing or
        NetworkConfigurationValidationCode.WiredCertificateRequired or
        NetworkConfigurationValidationCode.WiredCertificateMissing => ConfigurationNavigationTarget.EthernetDot1x,
        _ => ConfigurationNavigationTarget.Wifi
    };

    public static ConfigurationNavigationTarget ResolveRequiredNetworkSecret() =>
        ConfigurationNavigationTarget.Wifi;

    public static ConfigurationNavigationTarget ResolveAutopilot(AutopilotProvisioningMode mode) => mode switch
    {
        AutopilotProvisioningMode.HardwareHashUpload => ConfigurationNavigationTarget.AutopilotHardwareHashUpload,
        AutopilotProvisioningMode.InteractiveHardwareHashUpload => ConfigurationNavigationTarget.AutopilotInteractiveHardwareHashUpload,
        _ => ConfigurationNavigationTarget.AutopilotJsonProfile
    };

    public static ConfigurationNavigationTarget ResolveDeployFailure(
        AutopilotProvisioningMode mode,
        bool deploymentProtectionRequiresAttention = false) =>
        deploymentProtectionRequiresAttention
            ? ConfigurationNavigationTarget.General
            : ResolveAutopilot(mode);

}
