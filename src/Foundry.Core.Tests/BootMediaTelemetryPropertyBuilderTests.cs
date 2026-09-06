// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.Configuration;
using Foundry.Core.Services.Media;
using Foundry.Core.Services.Telemetry;
using Foundry.Core.Services.WinPe;
using Foundry.Telemetry;

namespace Foundry.Core.Tests;

public sealed class BootMediaTelemetryPropertyBuilderTests
{
    [Theory]
    [InlineData(false, null, "native")]
    [InlineData(false, "private-file-id", "native")]
    [InlineData(true, null, "native")]
    [InlineData(true, "private-file-id", "custom")]
    public void Build_ReportsUnattendConfigurationWithoutFileMetadata(bool enabled, string? defaultFileId, string expectedMode)
    {
        var document = new FoundryConfigurationDocument
        {
            Unattend = new UnattendSettings
            {
                IsEnabled = enabled,
                DefaultFileId = defaultFileId,
                Files =
                [
                    new UnattendFileSettings
                    {
                        Id = "private-file-id",
                        DisplayName = "private-file-label",
                        SourcePath = @"C:\private-file-source.xml",
                        ContentHash = "private-file-hash"
                    }
                ]
            },
            Customization = new CustomizationSettings
            {
                MachineNaming = new MachineNamingSettings { IsEnabled = true },
                Oobe = new OobeSettings { IsEnabled = true }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.Equal(enabled, result["unattend_enabled"]);
        Assert.Equal(expectedMode, result["unattend_default_mode"]);
        Assert.Equal(enabled ? 1 : 0, result["unattend_file_count"]);
        Assert.True((bool)result["customization_machine_naming_enabled"]!);
        Assert.True((bool)result["customization_oobe_enabled"]!);
        Assert.DoesNotContain(result.Values, value => value?.ToString()?.Contains("private-file", StringComparison.Ordinal) == true);
        IReadOnlyDictionary<string, object?> sanitized = TelemetryEventPropertyPolicy.Sanitize(TelemetryEvents.OsdBootMediaFinished, result);
        Assert.Equal(enabled, sanitized["unattend_enabled"]);
        Assert.Equal(expectedMode, sanitized["unattend_default_mode"]);
        Assert.Equal(enabled ? 1 : 0, sanitized["unattend_file_count"]);
    }

    [Theory]
    [InlineData(false, 150, 0)]
    [InlineData(true, 150, 100)]
    [InlineData(true, 0, 0)]
    public void Build_CountsOnlyActiveUnattendFilesWithinTelemetryBound(bool enabled, int fileCount, int expectedCount)
    {
        var document = new FoundryConfigurationDocument
        {
            Unattend = new UnattendSettings
            {
                IsEnabled = enabled,
                Files = Enumerable.Range(0, fileCount).Select(_ => new UnattendFileSettings()).ToArray()
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: false,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.Equal(expectedCount, result["unattend_file_count"]);
        Assert.Equal(enabled, result["customization_any_enabled"]);
    }

    [Fact]
    public void Build_WhenCreationFails_IncludesStableFailureTaxonomy()
    {
        var diagnostic = new WinPeDiagnostic(
            WinPeErrorCodes.IsoCreateFailed,
            "ISO creation failed.",
            failureKind: WinPeFailureKinds.Process,
            failureReason: WinPeFailureReasons.NonZeroExit,
            toolName: "MakeWinPEMedia",
            exitCode: 5);

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            new FoundryConfigurationDocument(),
            success: false,
            failedStepName: "Create ISO media",
            duration: TimeSpan.FromSeconds(3),
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            diagnostic);

        Assert.Equal(WinPeFailureKinds.Process, result["boot_media_failure_kind"]);
        Assert.Equal(WinPeFailureReasons.NonZeroExit, result["boot_media_failure_reason"]);
        Assert.Equal(WinPeErrorCodes.IsoCreateFailed, result["boot_media_failure_code"]);
        Assert.Equal("MakeWinPEMedia", result["boot_media_failure_tool"]);
        Assert.Equal(5, result["boot_media_failure_exit_code"]);
    }

    [Fact]
    public void Build_CreatesFinalBootMediaPropertiesWithoutSensitiveReadinessFlags()
    {
        var options = new MediaPreflightOptions
        {
            Architecture = WinPeArchitecture.Arm64,
            WinPeLanguage = "en-US",
            UsbPartitionStyle = UsbPartitionStyle.Gpt,
            UsbFormatMode = UsbFormatMode.Quick,
            BootImageSource = WinPeBootImageSource.WinReWifi,
            SignatureMode = WinPeSignatureMode.Pca2023,
            DriverVendors = [WinPeVendorSelection.Dell],
            CustomDriverDirectoryPath = @"C:\Drivers",
            IsAutopilotEnabled = true,
            AutopilotProvisioningMode = AutopilotProvisioningMode.HardwareHashUpload,
            AreRequiredSecretsReady = true
        };
        var document = new FoundryConfigurationDocument
        {
            General = new GeneralSettings
            {
                DeploymentProtection = new DeploymentProtectionSettings { IsEnabled = true }
            },
            Customization = new CustomizationSettings
            {
                MachineNaming = new MachineNamingSettings
                {
                    IsEnabled = true,
                    Mode = MachineNamingMode.Composed,
                    Components =
                    [
                        new MachineNameComponentSettings { Type = MachineNameComponentType.StaticText, StaticText = "LAB" },
                        new MachineNameComponentSettings
                        {
                            Type = MachineNameComponentType.SerialNumber,
                            MaximumLength = 8,
                            Truncation = MachineNameTruncation.KeepRight
                        }
                    ],
                    Separator = MachineNameSeparator.Hyphen,
                    Casing = MachineNameCasing.Uppercase,
                    AllowEditingDuringDeployment = true
                },
                Oobe = new OobeSettings
                {
                    IsEnabled = true,
                    SkipLicenseTerms = true,
                    DiagnosticDataLevel = OobeDiagnosticDataLevel.Off,
                    HidePrivacySetup = true,
                    AllowTailoredExperiences = false,
                    AllowAdvertisingId = false,
                    AllowOnlineSpeechRecognition = false,
                    AllowInkingAndTypingDiagnostics = false,
                    LocationAccess = OobeLocationAccessMode.ForceOff
                },
                AppxRemoval = new AppxRemovalSettings
                {
                    IsEnabled = true,
                    PackageNames = AppxRemovalCatalog.Entries
                        .Where(entry => entry.Category == "Gaming / Xbox")
                        .Select(entry => entry.PackageName)
                        .ToArray()
                },
                WindowsOptionalFeatures = new WindowsOptionalFeatureSettings
                {
                    IsEnabled = true,
                    EnabledFeatureIds = ["wf:netfx3"],
                    DisabledFeatureIds = ["wf:telnetclient"]
                },
                AiComponentRemoval = new AiComponentRemovalSettings
                {
                    IsEnabled = true,
                    RemoveCopilot = true,
                    RemoveAiHub = true,
                    DisableRecall = true,
                    DisableClickToDo = true,
                    DisableAiServiceAutoStart = true,
                    DisableEdgeAi = true,
                    DisablePaintAi = true,
                    DisableNotepadAi = true
                }
            },
            OperatingSystemSelection = new OperatingSystemSelectionSettings
            {
                IsEnabled = true,
                AllowedLanguageCodes = ["en-US", "fr-FR"],
                DefaultLanguageCode = "en-US",
                AllowedReleaseIds = ["25H2", "24H2"],
                DefaultReleaseId = "25H2",
                DefaultMediaOffset = 2,
                AllowedLicenseChannels = ["RET"],
                DefaultLicenseChannel = "RET",
                AllowedEditions = ["Pro", "Enterprise"],
                DefaultEdition = "Pro"
            },
            Localization = new LocalizationSettings
            {
                DefaultTimeZoneId = "Romance Standard Time"
            },
            Network = new NetworkSettings
            {
                Dot1x = new Dot1xSettings
                {
                    IsEnabled = true,
                    ProfileTemplatePath = @"C:\Network\wired.xml",
                    RequiresCertificate = true,
                    CertificatePath = @"C:\Network\wired.cer"
                },
                WifiProvisioned = true,
                Wifi = new WifiSettings
                {
                    IsEnabled = true,
                    Ssid = "CorpWifi",
                    SecurityType = NetworkConfigurationValidator.WifiSecurityPersonal
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Usb,
            TelemetryBootMediaUsbOperations.Create,
            options,
            document,
            success: false,
            failedStepName: "Customize boot image",
            duration: TimeSpan.FromSeconds(42.25),
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.Release,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.Debug);

        Assert.Equal(TelemetryBootMediaTargets.Usb, result["boot_media_target"]);
        Assert.Equal(TelemetryBootMediaUsbOperations.Create, result["boot_media_usb_operation"]);
        Assert.False((bool)result["boot_media_creation_success"]!);
        Assert.Equal(42.25, result["boot_media_creation_duration_seconds"]);
        Assert.Equal("Customize boot image", result["boot_media_creation_failed_step_name"]);
        Assert.Equal("arm64", result["boot_media_architecture"]);
        Assert.Equal("en-us", result["boot_media_winpe_language"]);
        Assert.Equal("winrewifi", result["boot_media_boot_image_source"]);
        Assert.Equal("pca2023", result["boot_media_signature_mode"]);
        Assert.Equal("gpt", result["boot_media_usb_partition_style"]);
        Assert.Equal("quick", result["boot_media_usb_format_mode"]);
        Assert.True((bool)result["boot_media_drivers_dell_enabled"]!);
        Assert.False((bool)result["boot_media_drivers_hp_enabled"]!);
        Assert.True((bool)result["boot_media_drivers_custom_enabled"]!);
        Assert.Equal(TelemetryRuntimePayloadSources.Release, result["boot_media_connect_runtime_payload_source"]);
        Assert.Equal(TelemetryRuntimePayloadSources.Debug, result["boot_media_deploy_runtime_payload_source"]);
        Assert.True((bool)result["autopilot_enabled"]!);
        Assert.Equal("hardware_hash_upload", result["autopilot_provisioning_mode"]);
        Assert.True((bool)result["deployment_protection_enabled"]!);
        Assert.True((bool)result["customization_any_enabled"]!);
        Assert.Equal("composed", result["customization_machine_naming_mode"]);
        Assert.Equal(2, result["customization_machine_naming_component_count"]);
        Assert.Equal("static_text,serial_number", result["customization_machine_naming_component_types"]);
        Assert.Equal("hyphen", result["customization_machine_naming_separator"]);
        Assert.Equal("uppercase", result["customization_machine_naming_casing"]);
        Assert.Equal("keep_right", result["customization_machine_naming_truncation_directions"]);
        Assert.True((bool)result["customization_machine_naming_editing_enabled"]!);
        Assert.False(result.ContainsKey("customization_machine_naming_prefix_configured"));
        Assert.True((bool)result["customization_appx_removal_enabled"]!);
        Assert.Equal(8, result["customization_appx_removal_package_count"]);
        Assert.Equal("gaming_xbox", result["customization_appx_removal_profile"]);
        Assert.True((bool)result["customization_windows_optional_features_enabled"]!);
        Assert.Equal(2, result["customization_windows_optional_features_configured_count"]);
        Assert.Equal(1, result["customization_windows_optional_features_enable_count"]);
        Assert.Equal(1, result["customization_windows_optional_features_disable_count"]);
        Assert.Equal(2, result["customization_windows_optional_features_category_count"]);
        Assert.True((bool)result["customization_windows_optional_features_requires_sxs"]!);
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
        Assert.False(result.ContainsKey("network_configured"));
        Assert.False(result.ContainsKey("connect_configured"));
        Assert.False(result.ContainsKey("deploy_configured"));
        Assert.DoesNotContain(result.Values.OfType<string>(), value => value.Contains("CorpWifi", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Values.OfType<string>(), value => value.Contains("Romance Standard Time", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Values.OfType<string>(), value => value.Contains("Microsoft.Xbox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_WhenWindowsOptionalFeaturesHaveNoValidActions_ReportsDisabledAggregates()
    {
        var document = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                WindowsOptionalFeatures = new WindowsOptionalFeatureSettings
                {
                    IsEnabled = true,
                    EnabledFeatureIds = ["wf:unknown"]
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.False((bool)result["customization_windows_optional_features_enabled"]!);
        Assert.Equal(0, result["customization_windows_optional_features_configured_count"]);
        Assert.Equal(0, result["customization_windows_optional_features_enable_count"]);
        Assert.Equal(0, result["customization_windows_optional_features_disable_count"]);
        Assert.Equal(0, result["customization_windows_optional_features_category_count"]);
        Assert.False((bool)result["customization_windows_optional_features_requires_sxs"]!);
        Assert.False((bool)result["customization_any_enabled"]!);
    }

    [Fact]
    public void Build_WhenWindowsOptionalFeaturesAreDisabled_DoesNotReportDormantSelections()
    {
        var document = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                WindowsOptionalFeatures = new WindowsOptionalFeatureSettings
                {
                    IsEnabled = false,
                    EnabledFeatureIds = ["wf:netfx3"],
                    DisabledFeatureIds = ["wf:telnetclient"]
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.False((bool)result["customization_windows_optional_features_enabled"]!);
        Assert.Equal(0, result["customization_windows_optional_features_configured_count"]);
        Assert.Equal(0, result["customization_windows_optional_features_enable_count"]);
        Assert.Equal(0, result["customization_windows_optional_features_disable_count"]);
        Assert.Equal(0, result["customization_windows_optional_features_category_count"]);
        Assert.False((bool)result["customization_windows_optional_features_requires_sxs"]!);
        Assert.False((bool)result["customization_any_enabled"]!);
    }

    [Fact]
    public void Build_WhenSourceBackedFeatureIsDisabled_DoesNotReportSourceRequirement()
    {
        var document = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                WindowsOptionalFeatures = new WindowsOptionalFeatureSettings
                {
                    IsEnabled = true,
                    DisabledFeatureIds = ["wf:netfx3"]
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.True((bool)result["customization_windows_optional_features_enabled"]!);
        Assert.Equal(1, result["customization_windows_optional_features_disable_count"]);
        Assert.False((bool)result["customization_windows_optional_features_requires_sxs"]!);
    }

    [Fact]
    public void Build_WhenOperatingSystemSelectionIsDisabled_DoesNotReportSavedPolicy()
    {
        var document = new FoundryConfigurationDocument
        {
            OperatingSystemSelection = new OperatingSystemSelectionSettings
            {
                IsEnabled = false,
                AllowedLanguageCodes = ["en-US"],
                DefaultLanguageCode = "en-US",
                AllowedReleaseIds = ["25H2"],
                DefaultReleaseId = "25H2",
                DefaultMediaOffset = 5,
                AllowedLicenseChannels = ["VOL"],
                DefaultLicenseChannel = "VOL",
                AllowedEditions = ["Enterprise"],
                DefaultEdition = "Enterprise"
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.False((bool)result["os_selection_enabled"]!);
        Assert.False((bool)result["os_selection_any_configured"]!);
        Assert.Equal(0, result["os_selection_allowed_languages_count"]);
        Assert.False((bool)result["os_selection_default_language_configured"]!);
        Assert.Equal(0, result["os_selection_allowed_release_count"]);
        Assert.False((bool)result["os_selection_default_release_configured"]!);
        Assert.Equal(0, result["os_selection_default_update_offset"]);
        Assert.Equal(0, result["os_selection_allowed_license_channel_count"]);
        Assert.False((bool)result["os_selection_default_license_channel_configured"]!);
        Assert.Equal(0, result["os_selection_allowed_edition_count"]);
        Assert.False((bool)result["os_selection_default_edition_configured"]!);
    }

    [Fact]
    public void Build_WhenOnlyOperatingSystemSelectionIsEnabled_ReportsCustomizationAnyEnabled()
    {
        var document = new FoundryConfigurationDocument
        {
            OperatingSystemSelection = new OperatingSystemSelectionSettings
            {
                IsEnabled = true
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.True((bool)result["customization_any_enabled"]!);
        Assert.True((bool)result["os_selection_enabled"]!);
        Assert.False((bool)result["os_selection_any_configured"]!);
    }

    [Fact]
    public void Build_WhenOnlyTimeZoneIsConfigured_DoesNotReportOsSelectionConfigured()
    {
        var document = new FoundryConfigurationDocument
        {
            Localization = new LocalizationSettings
            {
                DefaultTimeZoneId = "Romance Standard Time"
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.False((bool)result["os_selection_enabled"]!);
        Assert.False((bool)result["os_selection_any_configured"]!);
        Assert.Equal(0, result["os_selection_allowed_languages_count"]);
        Assert.False((bool)result["os_selection_default_language_configured"]!);
        Assert.True((bool)result["deployment_time_zone_configured"]!);
    }

    [Fact]
    public void Build_WhenAutopilotIsDisabled_ReportsDisabledProvisioningMode()
    {
        var options = new MediaPreflightOptions
        {
            IsAutopilotEnabled = false,
            AutopilotProvisioningMode = AutopilotProvisioningMode.HardwareHashUpload
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            options,
            new FoundryConfigurationDocument(),
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.False((bool)result["autopilot_enabled"]!);
        Assert.Equal("disabled", result["autopilot_provisioning_mode"]);
        Assert.Equal(TelemetryBootMediaUsbOperations.None, result["boot_media_usb_operation"]);
    }

    [Fact]
    public void Build_WhenInteractiveHardwareHashUploadIsEnabled_ReportsInteractiveMode()
    {
        var options = new MediaPreflightOptions
        {
            IsAutopilotEnabled = true,
            AutopilotProvisioningMode = AutopilotProvisioningMode.InteractiveHardwareHashUpload
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            options,
            new FoundryConfigurationDocument(),
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.Equal("interactive_hardware_hash_upload", result["autopilot_provisioning_mode"]);
    }

    [Fact]
    public void Build_WhenUsbBootMediaIsUpdated_ReportsUpdateOperation()
    {
        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Usb,
            TelemetryBootMediaUsbOperations.Update,
            new MediaPreflightOptions(),
            new FoundryConfigurationDocument(),
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.Release,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.Release);

        Assert.Equal(TelemetryBootMediaTargets.Usb, result["boot_media_target"]);
        Assert.Equal(TelemetryBootMediaUsbOperations.Update, result["boot_media_usb_operation"]);
    }

    [Fact]
    public void Build_WhenAppxSelectionDoesNotMatchSingleCategory_ReportsCustomProfile()
    {
        var options = new MediaPreflightOptions();
        var document = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                AppxRemoval = new AppxRemovalSettings
                {
                    IsEnabled = true,
                    PackageNames =
                    [
                        "Microsoft.BingWeather",
                        "Microsoft.GamingApp"
                    ]
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            options,
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.True((bool)result["customization_appx_removal_enabled"]!);
        Assert.Equal(2, result["customization_appx_removal_package_count"]);
        Assert.Equal("custom", result["customization_appx_removal_profile"]);
    }

    [Fact]
    public void Build_WhenAiComponentRemovalIsDisabled_DoesNotReportChildOptions()
    {
        var options = new MediaPreflightOptions();
        var document = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                AiComponentRemoval = new AiComponentRemovalSettings
                {
                    IsEnabled = false,
                    RemoveCopilot = true,
                    RemoveAiHub = true,
                    DisableRecall = true,
                    DisableClickToDo = true,
                    DisableAiServiceAutoStart = true,
                    DisableEdgeAi = true,
                    DisablePaintAi = true,
                    DisableNotepadAi = true
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            options,
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.False((bool)result["customization_ai_component_removal_enabled"]!);
        Assert.False((bool)result["customization_ai_remove_copilot_enabled"]!);
        Assert.False((bool)result["customization_ai_remove_ai_hub_enabled"]!);
        Assert.False((bool)result["customization_ai_disable_recall_enabled"]!);
        Assert.False((bool)result["customization_ai_disable_click_to_do_enabled"]!);
        Assert.False((bool)result["customization_ai_disable_service_autostart_enabled"]!);
        Assert.False((bool)result["customization_ai_disable_edge_ai_enabled"]!);
        Assert.False((bool)result["customization_ai_disable_paint_ai_enabled"]!);
        Assert.False((bool)result["customization_ai_disable_notepad_ai_enabled"]!);
        Assert.Equal(0, result["customization_ai_component_removal_option_count"]);
    }

    [Fact]
    public void Build_WhenAppxSelectionMatchesMultipleCategories_ReportsMultipleProfile()
    {
        var options = new MediaPreflightOptions();
        var document = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                AppxRemoval = new AppxRemovalSettings
                {
                    IsEnabled = true,
                    PackageNames = AppxRemovalCatalog.Entries
                        .Where(entry =>
                            entry.Category == "Gaming / Xbox" ||
                            entry.Category == "Phone / Cross-Device")
                        .Select(entry => entry.PackageName)
                        .ToArray()
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            options,
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.True((bool)result["customization_appx_removal_enabled"]!);
        Assert.Equal(10, result["customization_appx_removal_package_count"]);
        Assert.Equal("multiple", result["customization_appx_removal_profile"]);
    }

    [Fact]
    public void Build_WhenPersonalWifiIsNotProvisioned_DoesNotReportPassphraseConfigured()
    {
        var options = new MediaPreflightOptions
        {
            AreRequiredSecretsReady = true
        };
        var document = new FoundryConfigurationDocument
        {
            Network = new NetworkSettings
            {
                WifiProvisioned = false,
                Wifi = new WifiSettings
                {
                    IsEnabled = true,
                    Ssid = "CorpWifi",
                    SecurityType = NetworkConfigurationValidator.WifiSecurityPersonal
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            options,
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.False((bool)result["network_wifi_passphrase_configured"]!);
    }

    [Fact]
    public void Build_WhenOptionalCertificatesArePresent_ReportsCertificatesConfigured()
    {
        var options = new MediaPreflightOptions();
        var document = new FoundryConfigurationDocument
        {
            Network = new NetworkSettings
            {
                Dot1x = new Dot1xSettings
                {
                    IsEnabled = true,
                    RequiresCertificate = false,
                    CertificatePath = @"C:\Network\wired.cer"
                },
                Wifi = new WifiSettings
                {
                    IsEnabled = true,
                    HasEnterpriseProfile = true,
                    RequiresCertificate = false,
                    CertificatePath = @"C:\Network\wifi.cer"
                }
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            options,
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.True((bool)result["network_wired_dot1x_certificate_configured"]!);
        Assert.True((bool)result["network_wifi_enterprise_certificate_configured"]!);
    }

    [Fact]
    public void Build_WhenOnlyWiredNetworkProfileRoamingIsEnabled_ReportsAggregateAndTransportTelemetry()
    {
        var document = new FoundryConfigurationDocument
        {
            Network = new NetworkSettings
            {
                RoamWiredDot1xProfileToWindows = true,
                RoamWiredDot1xPrivateKeyMaterialToWindows = true,
                RoamWifiProfileToWindows = false
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.True((bool)result["network_any_enabled"]!);
        Assert.True((bool)result["network_profile_roaming_enabled"]!);
        Assert.True((bool)result["network_private_key_roaming_enabled"]!);
        Assert.True((bool)result["network_wired_dot1x_profile_roaming_enabled"]!);
        Assert.True((bool)result["network_wired_dot1x_private_key_roaming_enabled"]!);
        Assert.False((bool)result["network_wifi_profile_roaming_enabled"]!);
        Assert.False((bool)result["network_wifi_private_key_roaming_enabled"]!);
    }

    [Theory]
    [InlineData(false, 10, "manual", null)]
    [InlineData(true, 0, "immediate", null)]
    [InlineData(true, 42, "countdown", 42)]
    public void Build_ReportsAuthoredDeploymentRebootPolicy(
        bool automaticRebootEnabled,
        int automaticRebootDelaySeconds,
        string expectedMode,
        int? expectedDelaySeconds)
    {
        var document = new FoundryConfigurationDocument
        {
            General = new GeneralSettings
            {
                AutomaticRebootEnabled = automaticRebootEnabled,
                AutomaticRebootDelaySeconds = automaticRebootDelaySeconds
            }
        };

        IReadOnlyDictionary<string, object?> result = BootMediaTelemetryPropertyBuilder.Build(
            TelemetryBootMediaTargets.Iso,
            TelemetryBootMediaUsbOperations.None,
            new MediaPreflightOptions(),
            document,
            success: true,
            failedStepName: null,
            duration: TimeSpan.Zero,
            connectRuntimePayloadSource: TelemetryRuntimePayloadSources.None,
            deployRuntimePayloadSource: TelemetryRuntimePayloadSources.None);

        Assert.Equal(expectedMode, result["deployment_reboot_mode"]);

        if (expectedDelaySeconds.HasValue)
        {
            Assert.Equal(expectedDelaySeconds.Value, result["deployment_reboot_delay_seconds"]);
        }
        else
        {
            Assert.False(result.ContainsKey("deployment_reboot_delay_seconds"));
        }
    }
}
