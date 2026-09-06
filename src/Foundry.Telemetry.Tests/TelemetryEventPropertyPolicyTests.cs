// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Telemetry;

namespace Foundry.Telemetry.Tests;

public sealed class TelemetryEventPropertyPolicyTests
{
    [Theory]
    [InlineData(TelemetryEvents.OsdBootMediaFinished, "unattend_default_mode", "native")]
    [InlineData(TelemetryEvents.OsdBootMediaFinished, "unattend_default_mode", "custom")]
    [InlineData(TelemetryEvents.DeploySessionFinished, "deploy_unattend_mode", "native")]
    [InlineData(TelemetryEvents.DeploySessionFinished, "deploy_unattend_mode", "custom")]
    public void Sanitize_RetainsUnattendModeButDropsFileMetadata(string eventName, string modeProperty, string mode)
    {
        var properties = new Dictionary<string, object?>
        {
            [modeProperty] = mode,
            ["unattend_enabled"] = true,
            ["unattend_file_count"] = 2,
            ["unattend_file_name"] = "private.xml",
            ["unattend_display_name"] = "Private deployment",
            ["unattend_file_id"] = "private-id",
            ["unattend_source_path"] = @"C:\private.xml",
            ["unattend_content_hash"] = "private-hash",
            ["unattend_xml"] = "<password>private</password>"
        };

        IReadOnlyDictionary<string, object?> result = TelemetryEventPropertyPolicy.Sanitize(eventName, properties);

        Assert.Equal(mode, result[modeProperty]);
        Assert.Equal(eventName == TelemetryEvents.OsdBootMediaFinished ? 3 : 1, result.Count);
        Assert.DoesNotContain(result.Values, value => value?.ToString()?.Contains("private", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Theory]
    [InlineData(TelemetryEvents.OsdBootMediaFinished, "unattend_default_mode", "private.xml")]
    [InlineData(TelemetryEvents.DeploySessionFinished, "deploy_unattend_mode", "private.xml")]
    [InlineData(TelemetryEvents.OsdBootMediaFinished, "unattend_enabled", "private.xml")]
    [InlineData(TelemetryEvents.OsdBootMediaFinished, "unattend_file_count", "private.xml")]
    [InlineData(TelemetryEvents.ConnectSessionReady, "deploy_unattend_mode", "custom")]
    public void Sanitize_DropsInvalidOrMisplacedUnattendValues(string eventName, string property, string value)
    {
        IReadOnlyDictionary<string, object?> result = TelemetryEventPropertyPolicy.Sanitize(
            eventName, new Dictionary<string, object?> { [property] = value });

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Sanitize_DropsOutOfRangeUnattendFileCounts(int count)
    {
        Assert.Empty(TelemetryEventPropertyPolicy.Sanitize(TelemetryEvents.OsdBootMediaFinished,
            new Dictionary<string, object?> { ["unattend_file_count"] = count }));
    }

    [Fact]
    public void Sanitize_ForDailyActive_AllowsOnlyNonSensitiveProxyConfiguration()
    {
        Dictionary<string, object?> input = new()
        {
            ["proxy_method"] = "manual",
            ["proxy_authentication_mode"] = "explicit",
            ["proxy_address"] = "proxy.contoso.com",
            ["proxy_username"] = "admin"
        };

        IReadOnlyDictionary<string, object?> result = TelemetryEventPropertyPolicy.Sanitize(
            TelemetryEvents.AppDailyActive,
            input);

        Assert.Equal("manual", result["proxy_method"]);
        Assert.Equal("explicit", result["proxy_authentication_mode"]);
        Assert.False(result.ContainsKey("proxy_address"));
        Assert.False(result.ContainsKey("proxy_username"));
    }

    [Fact]
    public void Sanitize_ForBootMediaFinished_DropsPropertiesOutsideEventAllowlist()
    {
        Dictionary<string, object?> input = new()
        {
            ["boot_media_target"] = "iso",
            ["boot_media_usb_operation"] = "update",
            ["boot_media_creation_success"] = true,
            ["boot_media_creation_duration_seconds"] = 12.5,
            ["boot_media_architecture"] = "x64",
            ["boot_media_creation_failed_step_name"] = "Prepare WinPE workspace",
            ["boot_media_failure_kind"] = "process",
            ["boot_media_failure_reason"] = "nonzero_exit",
            ["boot_media_failure_code"] = "WINPE_BUILD_FAILED",
            ["boot_media_failure_tool"] = "copype",
            ["boot_media_failure_exit_code"] = 5,
            ["operation_id"] = "operation-1",
            ["boot_media_winpe_language"] = "en-us",
            ["boot_media_boot_image_source"] = "winpe_adk",
            ["boot_media_signature_mode"] = "signed",
            ["boot_media_usb_partition_style"] = "gpt",
            ["boot_media_usb_format_mode"] = "quick",
            ["boot_media_drivers_dell_enabled"] = true,
            ["boot_media_drivers_hp_enabled"] = false,
            ["boot_media_drivers_custom_enabled"] = true,
            ["boot_media_connect_runtime_payload_source"] = "release",
            ["boot_media_deploy_runtime_payload_source"] = "release",
            ["autopilot_enabled"] = true,
            ["autopilot_provisioning_mode"] = "hardware_hash_upload",
            ["network_configured"] = true,
            ["connect_configured"] = true,
            ["deploy_configured"] = true,
            ["os_selection_enabled"] = true,
            ["os_selection_any_configured"] = true,
            ["os_selection_allowed_languages_count"] = 2,
            ["os_selection_default_language_configured"] = true,
            ["os_selection_allowed_release_count"] = 2,
            ["os_selection_default_release_configured"] = true,
            ["os_selection_default_update_offset"] = 2,
            ["os_selection_allowed_license_channel_count"] = 1,
            ["os_selection_default_license_channel_configured"] = true,
            ["os_selection_allowed_edition_count"] = 2,
            ["os_selection_default_edition_configured"] = true,
            ["deployment_time_zone_configured"] = true,
            ["network_any_enabled"] = true,
            ["network_wired_dot1x_enabled"] = true,
            ["network_wired_dot1x_profile_configured"] = true,
            ["network_wired_dot1x_certificate_required"] = true,
            ["network_wired_dot1x_certificate_configured"] = true,
            ["network_wifi_provisioning_enabled"] = true,
            ["network_wifi_profile_configured"] = true,
            ["network_wifi_security_type"] = "personal",
            ["network_wifi_ssid_configured"] = true,
            ["network_wifi_passphrase_configured"] = true,
            ["network_wifi_enterprise_profile_configured"] = false,
            ["network_wifi_enterprise_certificate_required"] = false,
            ["network_wifi_enterprise_certificate_configured"] = false,
            ["network_profile_roaming_enabled"] = true,
            ["network_private_key_roaming_enabled"] = true,
            ["customization_any_enabled"] = true,
            ["customization_machine_naming_enabled"] = true,
            ["customization_machine_naming_mode"] = "composed",
            ["customization_machine_naming_component_count"] = 2,
            ["customization_machine_naming_component_types"] = "static_text,serial_number",
            ["customization_machine_naming_separator"] = "hyphen",
            ["customization_machine_naming_casing"] = "uppercase",
            ["customization_machine_naming_truncation_directions"] = "keep_right",
            ["customization_machine_naming_editing_enabled"] = true,
            ["customization_machine_naming_prefix_configured"] = true,
            ["customization_oobe_enabled"] = true,
            ["customization_oobe_skip_license_terms"] = true,
            ["customization_oobe_diagnostic_data_level"] = "off",
            ["customization_oobe_hide_privacy_setup"] = true,
            ["customization_oobe_tailored_experiences_enabled"] = false,
            ["customization_oobe_advertising_id_enabled"] = false,
            ["customization_oobe_online_speech_recognition_enabled"] = false,
            ["customization_oobe_inking_typing_diagnostics_enabled"] = false,
            ["customization_oobe_location_access"] = "force_off",
            ["customization_appx_removal_enabled"] = true,
            ["customization_appx_removal_package_count"] = 8,
            ["customization_appx_removal_profile"] = "gaming_xbox",
            ["customization_appx_removal_package_names"] = "Microsoft.XboxApp",
            ["customization_windows_optional_features_enabled"] = true,
            ["customization_windows_optional_features_configured_count"] = 2,
            ["customization_windows_optional_features_enable_count"] = 1,
            ["customization_windows_optional_features_disable_count"] = 1,
            ["customization_windows_optional_features_category_count"] = 2,
            ["customization_windows_optional_features_requires_sxs"] = true,
            ["customization_windows_optional_features_ids"] = "wf:netfx3",
            ["customization_windows_optional_features_source_path"] = @"C:\sources\sxs",
            ["customization_ai_component_removal_enabled"] = true,
            ["customization_ai_remove_copilot_enabled"] = true,
            ["customization_ai_remove_ai_hub_enabled"] = true,
            ["customization_ai_disable_recall_enabled"] = true,
            ["customization_ai_disable_click_to_do_enabled"] = true,
            ["customization_ai_disable_service_autostart_enabled"] = true,
            ["customization_ai_disable_edge_ai_enabled"] = true,
            ["customization_ai_disable_paint_ai_enabled"] = true,
            ["customization_ai_disable_notepad_ai_enabled"] = true,
            ["customization_ai_component_removal_option_count"] = 8,
            ["ssid"] = "CorpWifi",
            ["iso_output_path"] = @"C:\Temp\Foundry.iso"
        };

        IReadOnlyDictionary<string, object?> result = TelemetryEventPropertyPolicy.Sanitize(TelemetryEvents.OsdBootMediaFinished, input);

        Assert.Equal("iso", result["boot_media_target"]);
        Assert.Equal("update", result["boot_media_usb_operation"]);
        Assert.True((bool)result["boot_media_creation_success"]!);
        Assert.Equal(12.5, result["boot_media_creation_duration_seconds"]);
        Assert.Equal("x64", result["boot_media_architecture"]);
        Assert.Equal("Prepare WinPE workspace", result["boot_media_creation_failed_step_name"]);
        Assert.Equal("process", result["boot_media_failure_kind"]);
        Assert.Equal("nonzero_exit", result["boot_media_failure_reason"]);
        Assert.Equal("WINPE_BUILD_FAILED", result["boot_media_failure_code"]);
        Assert.Equal("copype", result["boot_media_failure_tool"]);
        Assert.Equal(5, result["boot_media_failure_exit_code"]);
        Assert.Equal("operation-1", result["operation_id"]);
        Assert.Equal("en-us", result["boot_media_winpe_language"]);
        Assert.Equal("winpe_adk", result["boot_media_boot_image_source"]);
        Assert.Equal("signed", result["boot_media_signature_mode"]);
        Assert.Equal("gpt", result["boot_media_usb_partition_style"]);
        Assert.Equal("quick", result["boot_media_usb_format_mode"]);
        Assert.True((bool)result["boot_media_drivers_dell_enabled"]!);
        Assert.False((bool)result["boot_media_drivers_hp_enabled"]!);
        Assert.True((bool)result["boot_media_drivers_custom_enabled"]!);
        Assert.Equal("release", result["boot_media_connect_runtime_payload_source"]);
        Assert.Equal("release", result["boot_media_deploy_runtime_payload_source"]);
        Assert.True((bool)result["autopilot_enabled"]!);
        Assert.Equal("hardware_hash_upload", result["autopilot_provisioning_mode"]);
        Assert.False(result.ContainsKey("network_configured"));
        Assert.False(result.ContainsKey("connect_configured"));
        Assert.False(result.ContainsKey("deploy_configured"));
        Assert.True((bool)result["os_selection_enabled"]!);
        Assert.True((bool)result["os_selection_any_configured"]!);
        Assert.Equal(2, result["os_selection_allowed_languages_count"]);
        Assert.True((bool)result["os_selection_default_language_configured"]!);
        Assert.Equal(2, result["os_selection_allowed_release_count"]);
        Assert.True((bool)result["os_selection_default_release_configured"]!);
        Assert.Equal(2, result["os_selection_default_update_offset"]);
        Assert.Equal(1, result["os_selection_allowed_license_channel_count"]);
        Assert.True((bool)result["os_selection_default_license_channel_configured"]!);
        Assert.Equal(2, result["os_selection_allowed_edition_count"]);
        Assert.True((bool)result["os_selection_default_edition_configured"]!);
        Assert.True((bool)result["deployment_time_zone_configured"]!);
        Assert.True((bool)result["network_any_enabled"]!);
        Assert.True((bool)result["network_wired_dot1x_enabled"]!);
        Assert.True((bool)result["network_wired_dot1x_profile_configured"]!);
        Assert.True((bool)result["network_wired_dot1x_certificate_required"]!);
        Assert.True((bool)result["network_wired_dot1x_certificate_configured"]!);
        Assert.True((bool)result["network_wifi_provisioning_enabled"]!);
        Assert.True((bool)result["network_wifi_profile_configured"]!);
        Assert.Equal("personal", result["network_wifi_security_type"]);
        Assert.True((bool)result["network_wifi_ssid_configured"]!);
        Assert.True((bool)result["network_wifi_passphrase_configured"]!);
        Assert.False((bool)result["network_wifi_enterprise_profile_configured"]!);
        Assert.False((bool)result["network_wifi_enterprise_certificate_required"]!);
        Assert.False((bool)result["network_wifi_enterprise_certificate_configured"]!);
        Assert.True((bool)result["network_profile_roaming_enabled"]!);
        Assert.True((bool)result["network_private_key_roaming_enabled"]!);
        Assert.True((bool)result["customization_any_enabled"]!);
        Assert.True((bool)result["customization_machine_naming_enabled"]!);
        Assert.Equal("composed", result["customization_machine_naming_mode"]);
        Assert.Equal(2, result["customization_machine_naming_component_count"]);
        Assert.Equal("static_text,serial_number", result["customization_machine_naming_component_types"]);
        Assert.Equal("hyphen", result["customization_machine_naming_separator"]);
        Assert.Equal("uppercase", result["customization_machine_naming_casing"]);
        Assert.Equal("keep_right", result["customization_machine_naming_truncation_directions"]);
        Assert.True((bool)result["customization_machine_naming_editing_enabled"]!);
        Assert.False(result.ContainsKey("customization_machine_naming_prefix_configured"));
        Assert.True((bool)result["customization_oobe_enabled"]!);
        Assert.True((bool)result["customization_oobe_skip_license_terms"]!);
        Assert.Equal("off", result["customization_oobe_diagnostic_data_level"]);
        Assert.True((bool)result["customization_oobe_hide_privacy_setup"]!);
        Assert.False((bool)result["customization_oobe_tailored_experiences_enabled"]!);
        Assert.False((bool)result["customization_oobe_advertising_id_enabled"]!);
        Assert.False((bool)result["customization_oobe_online_speech_recognition_enabled"]!);
        Assert.False((bool)result["customization_oobe_inking_typing_diagnostics_enabled"]!);
        Assert.Equal("force_off", result["customization_oobe_location_access"]);
        Assert.True((bool)result["customization_appx_removal_enabled"]!);
        Assert.Equal(8, result["customization_appx_removal_package_count"]);
        Assert.Equal("gaming_xbox", result["customization_appx_removal_profile"]);
        Assert.True((bool)result["customization_windows_optional_features_enabled"]!);
        Assert.Equal(2, result["customization_windows_optional_features_configured_count"]);
        Assert.Equal(1, result["customization_windows_optional_features_enable_count"]);
        Assert.Equal(1, result["customization_windows_optional_features_disable_count"]);
        Assert.Equal(2, result["customization_windows_optional_features_category_count"]);
        Assert.True((bool)result["customization_windows_optional_features_requires_sxs"]!);
        Assert.False(result.ContainsKey("customization_windows_optional_features_ids"));
        Assert.False(result.ContainsKey("customization_windows_optional_features_source_path"));
        Assert.True((bool)result["customization_ai_component_removal_enabled"]!);
        Assert.True((bool)result["customization_ai_remove_copilot_enabled"]!);
        Assert.True((bool)result["customization_ai_remove_ai_hub_enabled"]!);
        Assert.True((bool)result["customization_ai_disable_recall_enabled"]!);
        Assert.True((bool)result["customization_ai_disable_click_to_do_enabled"]!);
        Assert.True((bool)result["customization_ai_disable_service_autostart_enabled"]!);
        Assert.True((bool)result["customization_ai_disable_edge_ai_enabled"]!);
        Assert.True((bool)result["customization_ai_disable_paint_ai_enabled"]!);
        Assert.True((bool)result["customization_ai_disable_notepad_ai_enabled"]!);
        Assert.Equal(8, result["customization_ai_component_removal_option_count"]);
        Assert.False(result.ContainsKey("customization_appx_removal_package_names"));
        Assert.False(result.ContainsKey("ssid"));
        Assert.False(result.ContainsKey("iso_output_path"));
    }

    [Fact]
    public void Sanitize_ForDeploySessionFinished_DropsKnownDeploySensitiveValues()
    {
        Dictionary<string, object?> input = new()
        {
            ["operation_id"] = "deploy-operation-1",
            ["boot_media_target"] = "iso",
            ["deploy_runtime_payload_source"] = "release",
            ["deploy_session_success"] = false,
            ["deploy_session_cancelled"] = false,
            ["deploy_session_duration_seconds"] = 30,
            ["deploy_session_completed_step_count"] = 4,
            ["deploy_session_failed_step_name"] = "ApplyOperatingSystemImage",
            ["deploy_session_failed_operation_name"] = "boot.configure",
            ["deploy_session_failure_kind"] = "process",
            ["deploy_session_failure_code"] = "-193",
            ["deploy_session_failure_reason"] = "non_zero_exit",
            ["deploy_session_mode"] = "iso",
            ["deploy_session_dry_run_enabled"] = false,
            ["deploy_hardware_vendor"] = "dell",
            ["deploy_hardware_model"] = "latitude 5450",
            ["deploy_hardware_virtual_machine"] = false,
            ["deploy_os_product"] = "windows_11",
            ["deploy_os_version"] = "24h2",
            ["deploy_os_build"] = "26100",
            ["deploy_os_update_month"] = "2026-07",
            ["deploy_os_architecture"] = "x64",
            ["deploy_os_language"] = "en-us",
            ["deploy_os_edition"] = "pro",
            ["deploy_os_license_channel"] = "ret",
            ["deploy_os_image_index"] = 6,
            ["deploy_driver_pack_selection_kind"] = "oemcatalog",
            ["deploy_driver_pack_vendor"] = "dell",
            ["deploy_driver_pack_model"] = "latitude 5450",
            ["deploy_firmware_updates_enabled"] = true,
            ["deploy_autopilot_enabled"] = true,
            ["deploy_autopilot_provisioning_mode"] = "hardware_hash_upload",
            ["deploy_autopilot_hash_upload_state"] = "completed",
            ["deploy_autopilot_hash_group_tag_selected"] = true,
            ["operating_system_url"] = "https://example.invalid/os.wim",
            ["driver_pack_url"] = "https://example.invalid/driver.cab",
            ["target_computer_name"] = "PC-001",
            ["tenant_id"] = "tenant-id",
            ["certificate_thumbprint"] = "ABCDEF",
            ["serial_number"] = "SERIAL",
            ["hardware_hash"] = "HASH",
            ["group_tag"] = "KIOSK",
            ["exception"] = @"C:\Temp\failure.log"
        };

        IReadOnlyDictionary<string, object?> result = TelemetryEventPropertyPolicy.Sanitize(TelemetryEvents.DeploySessionFinished, input);

        Assert.False(result.ContainsKey("boot_media_target"));
        Assert.False(result.ContainsKey("deploy_runtime_payload_source"));
        Assert.Equal("deploy-operation-1", result["operation_id"]);
        Assert.Equal(false, result["deploy_session_success"]);
        Assert.Equal("ApplyOperatingSystemImage", result["deploy_session_failed_step_name"]);
        Assert.Equal("boot.configure", result["deploy_session_failed_operation_name"]);
        Assert.Equal("process", result["deploy_session_failure_kind"]);
        Assert.Equal("-193", result["deploy_session_failure_code"]);
        Assert.Equal("non_zero_exit", result["deploy_session_failure_reason"]);
        Assert.Equal("iso", result["deploy_session_mode"]);
        Assert.Equal("windows_11", result["deploy_os_product"]);
        Assert.Equal("2026-07", result["deploy_os_update_month"]);
        Assert.Equal("pro", result["deploy_os_edition"]);
        Assert.Equal("ret", result["deploy_os_license_channel"]);
        Assert.Equal(6, result["deploy_os_image_index"]);
        Assert.Equal("dell", result["deploy_driver_pack_vendor"]);
        Assert.Equal("latitude 5450", result["deploy_driver_pack_model"]);
        Assert.True((bool)result["deploy_firmware_updates_enabled"]!);
        Assert.True((bool)result["deploy_autopilot_enabled"]!);
        Assert.Equal("hardware_hash_upload", result["deploy_autopilot_provisioning_mode"]);
        Assert.Equal("completed", result["deploy_autopilot_hash_upload_state"]);
        Assert.True((bool)result["deploy_autopilot_hash_group_tag_selected"]!);
        Assert.False(result.ContainsKey("operating_system_url"));
        Assert.False(result.ContainsKey("driver_pack_url"));
        Assert.False(result.ContainsKey("target_computer_name"));
        Assert.False(result.ContainsKey("tenant_id"));
        Assert.False(result.ContainsKey("certificate_thumbprint"));
        Assert.False(result.ContainsKey("serial_number"));
        Assert.False(result.ContainsKey("hardware_hash"));
        Assert.False(result.ContainsKey("group_tag"));
        Assert.False(result.ContainsKey("exception"));
    }

    [Fact]
    public void Sanitize_ForConnectSessionReady_AllowsLayoutMode()
    {
        Dictionary<string, object?> input = new()
        {
            ["boot_media_target"] = "usb",
            ["connect_runtime_payload_source"] = "debug",
            ["connect_network_connection_type"] = "ethernet",
            ["connect_network_layout_mode"] = "ethernet_wifi",
            ["connect_ethernet_available"] = true,
            ["connect_wifi_available"] = true,
            ["connect_wifi_security_type"] = "none",
            ["connect_wifi_source"] = "none",
            ["connect_wired_dot1x_enabled"] = true,
            ["connect_wifi_provisioned"] = true,
            ["adapter_name"] = "Ethernet 1"
        };

        IReadOnlyDictionary<string, object?> result = TelemetryEventPropertyPolicy.Sanitize(TelemetryEvents.ConnectSessionReady, input);

        Assert.False(result.ContainsKey("boot_media_target"));
        Assert.False(result.ContainsKey("connect_runtime_payload_source"));
        Assert.Equal("ethernet", result["connect_network_connection_type"]);
        Assert.Equal("ethernet_wifi", result["connect_network_layout_mode"]);
        Assert.True((bool)result["connect_ethernet_available"]!);
        Assert.True((bool)result["connect_wifi_available"]!);
        Assert.Equal("none", result["connect_wifi_security_type"]);
        Assert.Equal("none", result["connect_wifi_source"]);
        Assert.True((bool)result["connect_wired_dot1x_enabled"]!);
        Assert.True((bool)result["connect_wifi_provisioned"]!);
        Assert.False(result.ContainsKey("adapter_name"));
        Assert.False(result.ContainsKey("success"));
    }

    [Fact]
    public void IsKnownEvent_ReturnsFalseForOldAndUnknownEventNames()
    {
        Assert.True(TelemetryEventPropertyPolicy.IsKnownEvent(TelemetryEvents.AppDailyActive));
        Assert.True(TelemetryEventPropertyPolicy.IsKnownEvent(TelemetryEvents.OsdBootMediaFinished));
        Assert.True(TelemetryEventPropertyPolicy.IsKnownEvent(TelemetryEvents.ConnectSessionReady));
        Assert.True(TelemetryEventPropertyPolicy.IsKnownEvent(TelemetryEvents.DeploySessionFinished));

        Assert.False(TelemetryEventPropertyPolicy.IsKnownEvent("app_started"));
        Assert.False(TelemetryEventPropertyPolicy.IsKnownEvent("boot_media_created"));
        Assert.False(TelemetryEventPropertyPolicy.IsKnownEvent("connect_network_ready"));
        Assert.False(TelemetryEventPropertyPolicy.IsKnownEvent("deployment_completed"));
        Assert.False(TelemetryEventPropertyPolicy.IsKnownEvent("unknown_event"));
    }

    [Fact]
    public void Sanitize_ForBootMediaFinished_RetainsOnlyApprovedRebootPolicyProperties()
    {
        Dictionary<string, object?> input = new()
        {
            ["deployment_reboot_mode"] = "countdown",
            ["deployment_reboot_delay_seconds"] = 42,
            ["deployment_protection_enabled"] = true,
            ["automatic_reboot_enabled"] = true
        };

        IReadOnlyDictionary<string, object?> result = TelemetryEventPropertyPolicy.Sanitize(TelemetryEvents.OsdBootMediaFinished, input);

        Assert.Equal(3, result.Count);
        Assert.Equal("countdown", result["deployment_reboot_mode"]);
        Assert.Equal(42, result["deployment_reboot_delay_seconds"]);
        Assert.True((bool)result["deployment_protection_enabled"]!);
        Assert.False(result.ContainsKey("automatic_reboot_enabled"));
    }

    [Fact]
    public void Sanitize_ForDeploySessionFinished_RetainsCompletionRebootTelemetryProperties()
    {
        Dictionary<string, object?> input = new()
        {
            ["deploy_completion_reboot_mode"] = "countdown",
            ["deploy_completion_reboot_delay_seconds"] = 42
        };

        IReadOnlyDictionary<string, object?> result = TelemetryEventPropertyPolicy.Sanitize(TelemetryEvents.DeploySessionFinished, input);

        Assert.Equal("countdown", result["deploy_completion_reboot_mode"]);
        Assert.Equal(42, result["deploy_completion_reboot_delay_seconds"]);
    }
}
