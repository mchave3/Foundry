// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.WinPe;
using Foundry.Telemetry;

namespace Foundry.Core.Services.Configuration;

/// <summary>
/// Restores safe defaults for malformed nullable values before configuration consumers dereference them.
/// </summary>
public static class FoundryConfigurationNormalizer
{
    public static FoundryConfigurationDocument Normalize(FoundryConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        GeneralSettings general = document.General ?? new GeneralSettings();
        NetworkSettings network = document.Network ?? new NetworkSettings();
        CustomizationSettings customization = document.Customization ?? new CustomizationSettings();
        UnattendSettings unattend = document.Unattend ?? new UnattendSettings();
        AutopilotSettings autopilot = document.Autopilot ?? new AutopilotSettings();

        return document with
        {
            General = NormalizeGeneral(general),
            Network = network with
            {
                Dot1x = network.Dot1x ?? new Dot1xSettings(),
                Wifi = network.Wifi ?? new WifiSettings()
            },
            OperatingSystemSelection = OperatingSystemSelectionSettingsNormalizer.Normalize(document.OperatingSystemSelection),
            Localization = document.Localization ?? new LocalizationSettings(),
            Customization = NormalizeCustomization(customization),
            Unattend = unattend with
            {
                Files = (unattend.Files ?? []).Where(static file => file is not null).ToArray()
            },
            Autopilot = NormalizeAutopilot(autopilot),
            Telemetry = document.Telemetry ?? new TelemetrySettings()
        };
    }

    private static GeneralSettings NormalizeGeneral(GeneralSettings settings)
    {
        return settings with
        {
            DeploymentProtection = settings.DeploymentProtection ?? new DeploymentProtectionSettings(),
            Architecture = Enum.IsDefined(settings.Architecture) ? settings.Architecture : WinPeArchitecture.X64,
            UsbPartitionStyle = Enum.IsDefined(settings.UsbPartitionStyle) ? settings.UsbPartitionStyle : UsbPartitionStyle.Gpt,
            UsbFormatMode = Enum.IsDefined(settings.UsbFormatMode) ? settings.UsbFormatMode : UsbFormatMode.Quick,
            AutomaticRebootDelaySeconds = DeploymentRebootDelay.NormalizeRuntime(settings.AutomaticRebootDelaySeconds)
        };
    }

    private static CustomizationSettings NormalizeCustomization(CustomizationSettings settings)
    {
        MachineNamingSettings machineNaming = settings.MachineNaming ?? new MachineNamingSettings();
        OobeSettings oobe = settings.Oobe ?? new OobeSettings();
        AppxRemovalSettings appxRemoval = settings.AppxRemoval ?? new AppxRemovalSettings();

        return settings with
        {
            MachineNaming = machineNaming with
            {
                Mode = Enum.IsDefined(machineNaming.Mode) ? machineNaming.Mode : MachineNamingMode.Manual,
                Separator = Enum.IsDefined(machineNaming.Separator) ? machineNaming.Separator : MachineNameSeparator.None,
                Casing = Enum.IsDefined(machineNaming.Casing) ? machineNaming.Casing : MachineNameCasing.Preserve,
                Components = (machineNaming.Components ?? []).Where(static component => component is not null).ToArray()
            },
            Oobe = oobe with
            {
                DiagnosticDataLevel = Enum.IsDefined(oobe.DiagnosticDataLevel) ? oobe.DiagnosticDataLevel : OobeDiagnosticDataLevel.Required,
                LocationAccess = Enum.IsDefined(oobe.LocationAccess) ? oobe.LocationAccess : OobeLocationAccessMode.UserControlled,
                AdditionalAccounts = (oobe.AdditionalAccounts ?? []).Where(static account => account is not null).ToArray()
            },
            AppxRemoval = appxRemoval with
            {
                PackageNames = (appxRemoval.PackageNames ?? []).Where(static packageName => !string.IsNullOrWhiteSpace(packageName)).ToArray()
            },
            WindowsOptionalFeatures = WindowsOptionalFeatureSettingsNormalizer.Normalize(settings.WindowsOptionalFeatures),
            AiComponentRemoval = settings.AiComponentRemoval ?? new AiComponentRemovalSettings()
        };
    }

    private static AutopilotSettings NormalizeAutopilot(AutopilotSettings settings)
    {
        AutopilotHardwareHashUploadSettings hardwareHashUpload = settings.HardwareHashUpload ?? new AutopilotHardwareHashUploadSettings();
        return settings with
        {
            ProvisioningMode = Enum.IsDefined(settings.ProvisioningMode)
                ? settings.ProvisioningMode
                : AutopilotProvisioningMode.JsonProfile,
            Profiles = (settings.Profiles ?? []).Where(static profile => profile is not null).ToArray(),
            HardwareHashUpload = hardwareHashUpload with
            {
                Tenant = hardwareHashUpload.Tenant ?? new AutopilotTenantRegistrationSettings(),
                KnownGroupTags = (hardwareHashUpload.KnownGroupTags ?? [])
                    .Where(static groupTag => !string.IsNullOrWhiteSpace(groupTag))
                    .ToArray(),
                BootMediaCertificate = hardwareHashUpload.BootMediaCertificate ?? new AutopilotBootMediaCertificateSettings()
            }
        };
    }
}
