// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.Configuration;
using Foundry.Core.Services.Media;
using Foundry.Core.Services.WinPe;
using Foundry.Telemetry;

namespace Foundry.Core.Services.Telemetry;

/// <summary>
/// Builds the event-specific properties for completed Foundry OSD boot media creation telemetry.
/// </summary>
public static class BootMediaTelemetryPropertyBuilder
{
    /// <summary>
    /// Creates the low-cardinality `osd:boot_media_finished` property set without sensitive configuration values.
    /// </summary>
    /// <param name="bootMediaTarget">Final media target value.</param>
    /// <param name="bootMediaUsbOperation">USB operation value.</param>
    /// <param name="options">Resolved media creation options.</param>
    /// <param name="document">Current Foundry configuration document.</param>
    /// <param name="success">Whether media creation completed successfully.</param>
    /// <param name="failedStepName">Failed media creation step name, or <see langword="null"/> when successful.</param>
    /// <param name="duration">Total media creation duration.</param>
    /// <param name="connectRuntimePayloadSource">Source of the generated Connect runtime payload.</param>
    /// <param name="deployRuntimePayloadSource">Source of the generated Deploy runtime payload.</param>
    /// <returns>Telemetry properties approved for `osd:boot_media_finished`.</returns>
    public static IReadOnlyDictionary<string, object?> Build(
        string bootMediaTarget,
        string bootMediaUsbOperation,
        MediaPreflightOptions options,
        FoundryConfigurationDocument document,
        bool success,
        string? failedStepName,
        TimeSpan duration,
        string connectRuntimePayloadSource,
        string deployRuntimePayloadSource,
        WinPeDiagnostic? diagnostic = null,
        string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(document);

        UnattendSettings unattend = document.Unattend ?? new UnattendSettings();
        DeploymentRebootTelemetryValue rebootPolicy = DeploymentRebootTelemetryValueResolver.Resolve(
            document.General.AutomaticRebootEnabled,
            document.General.AutomaticRebootDelaySeconds);

        var properties = new Dictionary<string, object?>
        {
            ["boot_media_target"] = bootMediaTarget,
            ["boot_media_usb_operation"] = bootMediaTarget == TelemetryBootMediaTargets.Usb
                ? bootMediaUsbOperation
                : TelemetryBootMediaUsbOperations.None,
            ["boot_media_creation_success"] = success,
            ["boot_media_creation_duration_seconds"] = Math.Round(duration.TotalSeconds, 2),
            ["boot_media_creation_failed_step_name"] = failedStepName,
            ["boot_media_failure_kind"] = diagnostic?.FailureKind,
            ["boot_media_failure_reason"] = diagnostic?.FailureReason,
            ["boot_media_failure_code"] = diagnostic?.Code,
            ["boot_media_failure_tool"] = diagnostic?.ToolName,
            ["boot_media_failure_exit_code"] = diagnostic?.ExitCode,
            ["operation_id"] = operationId,
            ["boot_media_architecture"] = options.Architecture.ToString().ToLowerInvariant(),
            ["boot_media_winpe_language"] = NormalizeCultureName(options.WinPeLanguage).ToLowerInvariant(),
            ["boot_media_boot_image_source"] = options.BootImageSource.ToString().ToLowerInvariant(),
            ["boot_media_signature_mode"] = options.SignatureMode.ToString().ToLowerInvariant(),
            ["boot_media_usb_partition_style"] = bootMediaTarget == TelemetryBootMediaTargets.Usb
                ? options.UsbPartitionStyle.ToString().ToLowerInvariant()
                : "none",
            ["boot_media_usb_format_mode"] = bootMediaTarget == TelemetryBootMediaTargets.Usb
                ? options.UsbFormatMode.ToString().ToLowerInvariant()
                : "none",
            ["boot_media_drivers_dell_enabled"] = options.DriverVendors.Contains(WinPeVendorSelection.Dell),
            ["boot_media_drivers_hp_enabled"] = options.DriverVendors.Contains(WinPeVendorSelection.Hp),
            ["boot_media_drivers_custom_enabled"] = !string.IsNullOrWhiteSpace(options.CustomDriverDirectoryPath),
            ["boot_media_connect_runtime_payload_source"] = connectRuntimePayloadSource,
            ["boot_media_deploy_runtime_payload_source"] = deployRuntimePayloadSource,
            ["autopilot_enabled"] = options.IsAutopilotEnabled,
            ["autopilot_provisioning_mode"] = ResolveAutopilotProvisioningMode(options),
            ["deployment_protection_enabled"] = document.General.DeploymentProtection.IsEnabled,
            ["unattend_enabled"] = unattend.IsEnabled,
            ["unattend_default_mode"] = unattend.IsEnabled && unattend.DefaultFileId is not null ? "custom" : "native",
            ["unattend_file_count"] = unattend.IsEnabled ? Math.Clamp(unattend.Files?.Count ?? 0, 0, 100) : 0,
            ["deployment_reboot_mode"] = rebootPolicy.Mode
        };

        if (rebootPolicy.DelaySeconds.HasValue)
        {
            properties["deployment_reboot_delay_seconds"] = rebootPolicy.DelaySeconds.Value;
        }

        AddCustomizationTelemetryProperties(properties, document.Customization);
        AddOperatingSystemSelectionTelemetryProperties(properties, document.OperatingSystemSelection);
        properties["customization_any_enabled"] =
            (bool)properties["customization_any_enabled"]! || document.OperatingSystemSelection.IsEnabled || unattend.IsEnabled;
        AddLocalizationTelemetryProperties(properties, document.Localization);
        AddNetworkTelemetryProperties(properties, document.Network, options.AreRequiredSecretsReady);

        return properties;
    }

    private static void AddCustomizationTelemetryProperties(
        IDictionary<string, object?> properties,
        CustomizationSettings customization)
    {
        MachineNamingSettings machineNaming = customization.MachineNaming;
        OobeSettings oobe = customization.Oobe;
        AppxRemovalSettings appxRemoval = customization.AppxRemoval;
        WindowsOptionalFeatureSettings windowsOptionalFeatures = WindowsOptionalFeatureSettingsNormalizer.Normalize(customization.WindowsOptionalFeatures);
        AiComponentRemovalSettings aiComponentRemoval = customization.AiComponentRemoval;
        string[] selectedAppxPackages = ResolveSelectedAppxPackages(appxRemoval);
        bool isAppxRemovalEnabled = appxRemoval.IsEnabled && selectedAppxPackages.Length > 0;
        int windowsOptionalFeatureEnableCount = windowsOptionalFeatures.EnabledFeatureIds.Count;
        int windowsOptionalFeatureDisableCount = windowsOptionalFeatures.DisabledFeatureIds.Count;
        int windowsOptionalFeatureConfiguredCount = windowsOptionalFeatureEnableCount + windowsOptionalFeatureDisableCount;
        bool areWindowsOptionalFeaturesEnabled = windowsOptionalFeatures.IsEnabled && windowsOptionalFeatureConfiguredCount > 0;
        string[] windowsOptionalFeatureIds = windowsOptionalFeatures.EnabledFeatureIds
            .Concat(windowsOptionalFeatures.DisabledFeatureIds)
            .ToArray();
        int windowsOptionalFeatureCategoryCount = areWindowsOptionalFeaturesEnabled
            ? windowsOptionalFeatureIds
                .Select(WindowsOptionalFeatureCatalog.Find)
                .Where(entry => entry is not null)
                .Select(entry => entry!.CategoryResourceKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
            : 0;
        bool windowsOptionalFeaturesRequireSxs = areWindowsOptionalFeaturesEnabled && windowsOptionalFeatures.EnabledFeatureIds
            .Select(WindowsOptionalFeatureCatalog.GetEffectiveEntry)
            .Any(entry => entry?.RequiresSetupMediaSxs == true);
        int aiComponentRemovalOptionCount = CountEnabledAiComponentRemovalOptions(aiComponentRemoval);
        bool isAiComponentRemovalEnabled = aiComponentRemoval.IsEnabled && aiComponentRemovalOptionCount > 0;

        properties["customization_any_enabled"] = machineNaming.IsEnabled || oobe.IsEnabled || isAppxRemovalEnabled || areWindowsOptionalFeaturesEnabled || isAiComponentRemovalEnabled;
        properties["customization_machine_naming_enabled"] = machineNaming.IsEnabled;
        properties["customization_machine_naming_mode"] = ResolveMachineNamingTelemetryMode(machineNaming);
        properties["customization_machine_naming_component_count"] =
            machineNaming.IsEnabled && machineNaming.Mode == MachineNamingMode.Composed ? machineNaming.Components.Count : 0;
        properties["customization_machine_naming_component_types"] =
            ResolveMachineNamingComponentTypes(machineNaming);
        properties["customization_machine_naming_separator"] = ToTelemetryValue(machineNaming.Separator);
        properties["customization_machine_naming_casing"] = ToTelemetryValue(machineNaming.Casing);
        properties["customization_machine_naming_truncation_directions"] =
            ResolveMachineNamingTruncationDirections(machineNaming);
        properties["customization_machine_naming_editing_enabled"] =
            machineNaming.IsEnabled && machineNaming.AllowEditingDuringDeployment;
        properties["customization_oobe_enabled"] = oobe.IsEnabled;
        properties["customization_oobe_skip_license_terms"] = oobe.IsEnabled && oobe.SkipLicenseTerms;
        properties["customization_oobe_diagnostic_data_level"] = ToTelemetryValue(oobe.DiagnosticDataLevel);
        properties["customization_oobe_hide_privacy_setup"] = oobe.IsEnabled && oobe.HidePrivacySetup;
        properties["customization_oobe_tailored_experiences_enabled"] = oobe.IsEnabled && oobe.AllowTailoredExperiences;
        properties["customization_oobe_advertising_id_enabled"] = oobe.IsEnabled && oobe.AllowAdvertisingId;
        properties["customization_oobe_online_speech_recognition_enabled"] = oobe.IsEnabled && oobe.AllowOnlineSpeechRecognition;
        properties["customization_oobe_inking_typing_diagnostics_enabled"] = oobe.IsEnabled && oobe.AllowInkingAndTypingDiagnostics;
        properties["customization_oobe_location_access"] = ToTelemetryValue(oobe.LocationAccess);
        properties["customization_appx_removal_enabled"] = isAppxRemovalEnabled;
        properties["customization_appx_removal_package_count"] = isAppxRemovalEnabled ? selectedAppxPackages.Length : 0;
        properties["customization_appx_removal_profile"] = ResolveAppxRemovalProfile(selectedAppxPackages, isAppxRemovalEnabled);
        properties["customization_windows_optional_features_enabled"] = areWindowsOptionalFeaturesEnabled;
        properties["customization_windows_optional_features_configured_count"] = areWindowsOptionalFeaturesEnabled ? windowsOptionalFeatureConfiguredCount : 0;
        properties["customization_windows_optional_features_enable_count"] = areWindowsOptionalFeaturesEnabled ? windowsOptionalFeatureEnableCount : 0;
        properties["customization_windows_optional_features_disable_count"] = areWindowsOptionalFeaturesEnabled ? windowsOptionalFeatureDisableCount : 0;
        properties["customization_windows_optional_features_category_count"] = windowsOptionalFeatureCategoryCount;
        properties["customization_windows_optional_features_requires_sxs"] = windowsOptionalFeaturesRequireSxs;
        properties["customization_ai_component_removal_enabled"] = isAiComponentRemovalEnabled;
        properties["customization_ai_remove_copilot_enabled"] = isAiComponentRemovalEnabled && aiComponentRemoval.RemoveCopilot;
        properties["customization_ai_remove_ai_hub_enabled"] = isAiComponentRemovalEnabled && aiComponentRemoval.RemoveAiHub;
        properties["customization_ai_disable_recall_enabled"] = isAiComponentRemovalEnabled && aiComponentRemoval.DisableRecall;
        properties["customization_ai_disable_click_to_do_enabled"] = isAiComponentRemovalEnabled && aiComponentRemoval.DisableClickToDo;
        properties["customization_ai_disable_service_autostart_enabled"] = isAiComponentRemovalEnabled && aiComponentRemoval.DisableAiServiceAutoStart;
        properties["customization_ai_disable_edge_ai_enabled"] = isAiComponentRemovalEnabled && aiComponentRemoval.DisableEdgeAi;
        properties["customization_ai_disable_paint_ai_enabled"] = isAiComponentRemovalEnabled && aiComponentRemoval.DisablePaintAi;
        properties["customization_ai_disable_notepad_ai_enabled"] = isAiComponentRemovalEnabled && aiComponentRemoval.DisableNotepadAi;
        properties["customization_ai_component_removal_option_count"] = isAiComponentRemovalEnabled ? aiComponentRemovalOptionCount : 0;
    }

    private static void AddOperatingSystemSelectionTelemetryProperties(
        IDictionary<string, object?> properties,
        OperatingSystemSelectionSettings operatingSystemSelection)
    {
        bool isEnabled = operatingSystemSelection.IsEnabled;
        int allowedLanguagesCount = isEnabled ? operatingSystemSelection.AllowedLanguageCodes.Count : 0;
        bool hasDefaultLanguage = isEnabled && !string.IsNullOrWhiteSpace(operatingSystemSelection.DefaultLanguageCode);
        int allowedReleaseCount = isEnabled ? operatingSystemSelection.AllowedReleaseIds.Count : 0;
        bool hasDefaultRelease = isEnabled && !string.IsNullOrWhiteSpace(operatingSystemSelection.DefaultReleaseId);
        int defaultUpdateOffset = isEnabled ? Math.Clamp(operatingSystemSelection.DefaultMediaOffset, 0, 11) : 0;
        int allowedLicenseChannelCount = isEnabled ? operatingSystemSelection.AllowedLicenseChannels.Count : 0;
        bool hasDefaultLicenseChannel = isEnabled && !string.IsNullOrWhiteSpace(operatingSystemSelection.DefaultLicenseChannel);
        int allowedEditionCount = isEnabled ? operatingSystemSelection.AllowedEditions.Count : 0;
        bool hasDefaultEdition = isEnabled && !string.IsNullOrWhiteSpace(operatingSystemSelection.DefaultEdition);

        properties["os_selection_enabled"] = isEnabled;
        properties["os_selection_any_configured"] =
            allowedLanguagesCount > 0 ||
            hasDefaultLanguage ||
            allowedReleaseCount > 0 ||
            hasDefaultRelease ||
            defaultUpdateOffset > 0 ||
            allowedLicenseChannelCount > 0 ||
            hasDefaultLicenseChannel ||
            allowedEditionCount > 0 ||
            hasDefaultEdition;
        properties["os_selection_allowed_languages_count"] = allowedLanguagesCount;
        properties["os_selection_default_language_configured"] = hasDefaultLanguage;
        properties["os_selection_allowed_release_count"] = allowedReleaseCount;
        properties["os_selection_default_release_configured"] = hasDefaultRelease;
        properties["os_selection_default_update_offset"] = defaultUpdateOffset;
        properties["os_selection_allowed_license_channel_count"] = allowedLicenseChannelCount;
        properties["os_selection_default_license_channel_configured"] = hasDefaultLicenseChannel;
        properties["os_selection_allowed_edition_count"] = allowedEditionCount;
        properties["os_selection_default_edition_configured"] = hasDefaultEdition;
    }

    private static void AddLocalizationTelemetryProperties(
        IDictionary<string, object?> properties,
        LocalizationSettings localization)
    {
        properties["deployment_time_zone_configured"] = !string.IsNullOrWhiteSpace(localization.DefaultTimeZoneId);
    }

    private static void AddNetworkTelemetryProperties(
        IDictionary<string, object?> properties,
        NetworkSettings network,
        bool areRequiredSecretsReady)
    {
        Dot1xSettings dot1x = network.Dot1x;
        WifiSettings wifi = network.Wifi;
        bool isDot1xProfileConfigured = dot1x.IsEnabled && !string.IsNullOrWhiteSpace(dot1x.ProfileTemplatePath);
        bool isDot1xCertificateConfigured = dot1x.IsEnabled && !string.IsNullOrWhiteSpace(dot1x.CertificatePath);
        bool isWifiEnterpriseProfileConfigured = wifi.IsEnabled && wifi.HasEnterpriseProfile && !string.IsNullOrWhiteSpace(wifi.EnterpriseProfileTemplatePath);
        bool isWifiEnterpriseCertificateConfigured = wifi.IsEnabled && !string.IsNullOrWhiteSpace(wifi.CertificatePath);

        bool isAnyProfileRoamingEnabled = network.RoamWiredDot1xProfileToWindows || network.RoamWifiProfileToWindows;
        bool isAnyPrivateKeyRoamingEnabled = network.RoamWiredDot1xPrivateKeyMaterialToWindows || network.RoamWifiPrivateKeyMaterialToWindows;
        properties["network_any_enabled"] = dot1x.IsEnabled || network.WifiProvisioned || wifi.IsEnabled || isAnyProfileRoamingEnabled;
        properties["network_profile_roaming_enabled"] = isAnyProfileRoamingEnabled;
        properties["network_private_key_roaming_enabled"] = isAnyPrivateKeyRoamingEnabled;
        properties["network_wired_dot1x_profile_roaming_enabled"] = network.RoamWiredDot1xProfileToWindows;
        properties["network_wired_dot1x_private_key_roaming_enabled"] = network.RoamWiredDot1xPrivateKeyMaterialToWindows;
        properties["network_wifi_profile_roaming_enabled"] = network.RoamWifiProfileToWindows;
        properties["network_wifi_private_key_roaming_enabled"] = network.RoamWifiPrivateKeyMaterialToWindows;
        properties["network_wired_dot1x_enabled"] = dot1x.IsEnabled;
        properties["network_wired_dot1x_profile_configured"] = isDot1xProfileConfigured;
        properties["network_wired_dot1x_certificate_required"] = dot1x.IsEnabled && dot1x.RequiresCertificate;
        properties["network_wired_dot1x_certificate_configured"] = isDot1xCertificateConfigured;
        properties["network_wifi_provisioning_enabled"] = network.WifiProvisioned;
        properties["network_wifi_profile_configured"] = wifi.IsEnabled &&
            (!string.IsNullOrWhiteSpace(wifi.Ssid) || isWifiEnterpriseProfileConfigured);
        properties["network_wifi_security_type"] = ResolveNetworkWifiSecurityTelemetryValue(wifi);
        properties["network_wifi_ssid_configured"] = wifi.IsEnabled && !string.IsNullOrWhiteSpace(wifi.Ssid);
        properties["network_wifi_passphrase_configured"] = RequiresPersonalWifiPassphrase(wifi) && areRequiredSecretsReady && network.WifiProvisioned;
        properties["network_wifi_enterprise_profile_configured"] = isWifiEnterpriseProfileConfigured;
        properties["network_wifi_enterprise_certificate_required"] = wifi.IsEnabled && wifi.RequiresCertificate;
        properties["network_wifi_enterprise_certificate_configured"] = isWifiEnterpriseCertificateConfigured;
    }

    private static string ResolveMachineNamingTelemetryMode(MachineNamingSettings settings)
    {
        if (!settings.IsEnabled)
        {
            return "disabled";
        }

        return ToTelemetryValue(settings.Mode);
    }

    private static string ResolveMachineNamingComponentTypes(MachineNamingSettings settings) =>
        settings.IsEnabled && settings.Mode == MachineNamingMode.Composed
            ? string.Join(',', settings.Components.Select(component => ToTelemetryValue(component.Type)))
            : string.Empty;

    private static string ResolveMachineNamingTruncationDirections(MachineNamingSettings settings) =>
        settings.IsEnabled && settings.Mode == MachineNamingMode.Composed
            ? string.Join(',', settings.Components
                .Where(component => component.Truncation is not null)
                .Select(component => ToTelemetryValue(component.Truncation!.Value)))
            : string.Empty;

    private static string ResolveAutopilotProvisioningMode(MediaPreflightOptions options)
    {
        if (!options.IsAutopilotEnabled)
        {
            return "disabled";
        }

        return options.AutopilotProvisioningMode switch
        {
            AutopilotProvisioningMode.HardwareHashUpload => "hardware_hash_upload",
            AutopilotProvisioningMode.InteractiveHardwareHashUpload => "interactive_hardware_hash_upload",
            _ => "json_profile"
        };
    }

    private static string[] ResolveSelectedAppxPackages(AppxRemovalSettings appxRemoval)
    {
        if (!appxRemoval.IsEnabled)
        {
            return [];
        }

        return appxRemoval.PackageNames
            .Where(packageName => !string.IsNullOrWhiteSpace(packageName))
            .Select(packageName => packageName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(packageName => packageName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CountEnabledAiComponentRemovalOptions(AiComponentRemovalSettings settings)
    {
        int count = 0;

        count += settings.RemoveCopilot ? 1 : 0;
        count += settings.RemoveAiHub ? 1 : 0;
        count += settings.DisableRecall ? 1 : 0;
        count += settings.DisableClickToDo ? 1 : 0;
        count += settings.DisableAiServiceAutoStart ? 1 : 0;
        count += settings.DisableEdgeAi ? 1 : 0;
        count += settings.DisablePaintAi ? 1 : 0;
        count += settings.DisableNotepadAi ? 1 : 0;

        return count;
    }

    private static string ResolveAppxRemovalProfile(string[] selectedPackageNames, bool isEnabled)
    {
        if (!isEnabled)
        {
            return "none";
        }

        var selectedPackages = selectedPackageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] selectedProfileNames = InferAppxRemovalProfileNames(selectedPackages).ToArray();
        if (selectedProfileNames.Length == 0)
        {
            return "custom";
        }

        if (selectedProfileNames.Length == 1)
        {
            return ToTelemetryToken(selectedProfileNames[0]);
        }

        return "multiple";
    }

    private static IEnumerable<string> InferAppxRemovalProfileNames(HashSet<string> selectedPackages)
    {
        int matchedPackageCount = 0;
        var profileNames = new List<string>();
        foreach (IGrouping<string, AppxRemovalCatalogEntry> category in AppxRemovalCatalog.Entries.GroupBy(entry => entry.Category))
        {
            string[] categoryPackages = category
                .Select(entry => entry.PackageName)
                .ToArray();
            int selectedCategoryPackageCount = categoryPackages.Count(selectedPackages.Contains);
            if (selectedCategoryPackageCount == 0)
            {
                continue;
            }

            if (selectedCategoryPackageCount != categoryPackages.Length)
            {
                yield break;
            }

            matchedPackageCount += selectedCategoryPackageCount;
            profileNames.Add(category.Key);
        }

        if (matchedPackageCount != selectedPackages.Count)
        {
            yield break;
        }

        foreach (string profileName in profileNames)
        {
            yield return profileName;
        }
    }

    private static string ToTelemetryToken(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        bool previousWasSeparator = false;

        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    private static string ToTelemetryValue(OobeDiagnosticDataLevel value)
    {
        return value switch
        {
            OobeDiagnosticDataLevel.Optional => "optional",
            OobeDiagnosticDataLevel.Off => "off",
            _ => "required"
        };
    }

    private static string ToTelemetryValue(OobeLocationAccessMode value)
    {
        return value switch
        {
            OobeLocationAccessMode.ForceOff => "force_off",
            _ => "user_controlled"
        };
    }

    private static string ToTelemetryValue(MachineNamingMode value) => value switch
    {
        MachineNamingMode.Composed => "composed",
        _ => "manual"
    };

    private static string ToTelemetryValue(MachineNameSeparator value) => value switch
    {
        MachineNameSeparator.Hyphen => "hyphen",
        _ => "none"
    };

    private static string ToTelemetryValue(MachineNameCasing value) => value switch
    {
        MachineNameCasing.Uppercase => "uppercase",
        MachineNameCasing.Lowercase => "lowercase",
        _ => "preserve"
    };

    private static string ToTelemetryValue(MachineNameComponentType value) => value switch
    {
        MachineNameComponentType.StaticText => "static_text",
        MachineNameComponentType.SerialNumber => "serial_number",
        MachineNameComponentType.Manufacturer => "manufacturer",
        MachineNameComponentType.Model => "model",
        MachineNameComponentType.AssetTag => "asset_tag",
        MachineNameComponentType.SystemUuid => "system_uuid",
        MachineNameComponentType.Random => "random",
        _ => "unknown"
    };

    private static string ToTelemetryValue(MachineNameTruncation value) => value switch
    {
        MachineNameTruncation.KeepRight => "keep_right",
        _ => "keep_left"
    };

    private static string ResolveNetworkWifiSecurityTelemetryValue(WifiSettings wifi)
    {
        if (!wifi.IsEnabled)
        {
            return "none";
        }

        string normalizedSecurity = NetworkConfigurationValidator.NormalizeWifiSecurityType(wifi);
        return normalizedSecurity switch
        {
            NetworkConfigurationValidator.WifiSecurityOpen => "open",
            NetworkConfigurationValidator.WifiSecurityOwe => "owe",
            NetworkConfigurationValidator.WifiSecurityPersonal => "personal",
            NetworkConfigurationValidator.WifiSecurityEnterprise => "enterprise",
            NetworkConfigurationValidator.WifiSecurityEnterpriseWpa3 => "enterprise",
            NetworkConfigurationValidator.WifiSecurityEnterpriseWpa3192 => "enterprise",
            _ => "unknown"
        };
    }

    private static bool RequiresPersonalWifiPassphrase(WifiSettings wifi)
    {
        return wifi.IsEnabled &&
            !wifi.HasEnterpriseProfile &&
            string.Equals(
                NetworkConfigurationValidator.NormalizeWifiSecurityType(wifi),
                NetworkConfigurationValidator.WifiSecurityPersonal,
                StringComparison.Ordinal);
    }

    private static string NormalizeCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return "unknown";
        }

        try
        {
            return CultureInfo.GetCultureInfo(cultureName.Trim()).Name;
        }
        catch (CultureNotFoundException)
        {
            return cultureName.Trim();
        }
    }
}
