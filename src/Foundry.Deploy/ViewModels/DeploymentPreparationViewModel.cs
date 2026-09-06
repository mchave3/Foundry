// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Localization;
using Foundry.Deploy.Services.Runtime;
using Foundry.Deploy.Services.System;
using ComputerNameRules = Foundry.Core.Services.Configuration.ComputerNameRules;

namespace Foundry.Deploy.ViewModels;

/// <summary>
/// Represents one selectable hardware hash upload group tag option.
/// </summary>
/// <param name="DisplayName">Localized display name shown to the operator.</param>
/// <param name="GroupTag">The group tag value to upload, or null when no group tag should be used.</param>
public sealed record HardwareHashGroupTagOption(string DisplayName, string? GroupTag);

public sealed partial class DeploymentPreparationViewModel : LocalizedViewModelBase
{
    private const string DebugAutopilotProfileFolderName = "DebugAutopilotProfile";
    private const string DebugAutopilotProfileDisplayName = "Debug Autopilot Profile";
    private const string DebugAutopilotGroupTag = "Debug";

    private readonly bool _isDebugSafeMode;
    private HardwareProfile? _detectedHardware;
    private DeployMachineNamingSettings _machineNamingConfiguration = new();
    private MachineNamePreparationResult? _machineNamePreparationFailure;
    private string _detectedHardwareSummaryRaw = "Detecting hardware...";
    private bool _isApplyingComputerName;
    private bool _isUpdatingFirmwareOptionSelection;
    private bool _hasUserSelectedFirmwareOption;
    private bool _firmwareUpdatesPreference = true;

    public DeploymentPreparationViewModel(
        ILocalizationService localizationService,
        bool isDebugSafeMode,
        Foundry.Deploy.Services.Deployment.Unattend.UnattendContentService? unattendContentService = null)
        : base(localizationService)
    {
        _isDebugSafeMode = isDebugSafeMode;
        _unattendContentService = unattendContentService;
        LocalizationService.LanguageChanged += OnLocalizationLanguageChanged;
    }

    public event EventHandler? StateChanged;

    [ObservableProperty]
    private string targetComputerName = string.Empty;

    [ObservableProperty]
    private bool isTargetComputerNameReadOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetComputerNameValidationError))]
    [NotifyPropertyChangedFor(nameof(IsTargetComputerNameValid))]
    private string targetComputerNameValidationMessage = string.Empty;

    [ObservableProperty]
    private TargetDiskInfo? selectedTargetDisk;

    [ObservableProperty]
    private string cacheRootPath = string.Empty;

    [ObservableProperty]
    private bool applyFirmwareUpdates = true;

    [ObservableProperty]
    private bool isAutopilotEnabled;

    [ObservableProperty]
    private AutopilotProvisioningMode autopilotProvisioningMode = AutopilotProvisioningMode.JsonProfile;

    [ObservableProperty]
    private DeployAutopilotHardwareHashUploadSettings autopilotHardwareHashUpload = new();

    [ObservableProperty]
    private HardwareHashGroupTagOption? selectedHardwareHashGroupTag;

    [ObservableProperty]
    private AutopilotProfileCatalogItem? selectedAutopilotProfile;

    [ObservableProperty]
    private string detectedHardwareSummary = LocalizationText.GetString("Preparation.DetectingHardware");

    [ObservableProperty]
    private bool isTargetDiskLoading;

    public ObservableCollection<TargetDiskInfo> TargetDisks { get; } = [];
    public ObservableCollection<AutopilotProfileCatalogItem> AutopilotProfiles { get; } = [];
    public ObservableCollection<HardwareHashGroupTagOption> HardwareHashGroupTagOptions { get; } = [];

    public bool IsFirmwareUpdatesOptionEnabled => _detectedHardware?.IsVirtualMachine != true;
    public bool HasAutopilotProfiles => AutopilotProfiles.Count > 0;
    public bool IsJsonProfileMode => AutopilotProvisioningMode == AutopilotProvisioningMode.JsonProfile;
    public bool IsHardwareHashUploadMode => AutopilotProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload;
    public bool IsJsonProfileControlsVisible => IsAutopilotEnabled && IsJsonProfileMode;
    public bool IsHardwareHashUploadControlsVisible => IsAutopilotEnabled && IsHardwareHashUploadMode;
    public bool IsAutopilotProfileSelectionEnabled => IsJsonProfileControlsVisible && HasAutopilotProfiles;
    public bool HasHardwareHashUploadMetadata =>
        !string.IsNullOrWhiteSpace(AutopilotHardwareHashUpload.TenantId) &&
        !string.IsNullOrWhiteSpace(AutopilotHardwareHashUpload.ClientId) &&
        !string.IsNullOrWhiteSpace(AutopilotHardwareHashUpload.ActiveCertificateKeyId) &&
        !string.IsNullOrWhiteSpace(AutopilotHardwareHashUpload.ActiveCertificateThumbprint) &&
        AutopilotHardwareHashUpload.ActiveCertificateExpiresOnUtc is not null;
    public bool IsHardwareHashCertificateExpired =>
        AutopilotHardwareHashUpload.ActiveCertificateExpiresOnUtc is DateTimeOffset expiresOn &&
        expiresOn <= DateTimeOffset.UtcNow;
    public bool IsHardwareHashCertificateUsable => HasHardwareHashUploadMetadata && !IsHardwareHashCertificateExpired;
    public bool IsHardwareHashGroupTagControlsVisible => IsHardwareHashUploadControlsVisible && IsHardwareHashCertificateUsable;
    public bool IsHardwareHashUploadMessageVisible => IsHardwareHashUploadControlsVisible && !IsHardwareHashCertificateUsable;
    public string AutopilotHardwareHashTenantIdText => NormalizeMetadataValue(AutopilotHardwareHashUpload.TenantId);
    public string AutopilotHardwareHashCertificateExpirationText =>
        AutopilotHardwareHashUpload.ActiveCertificateExpiresOnUtc is DateTimeOffset expiresOn
            ? expiresOn.ToLocalTime().ToString("g", LocalizationService.CurrentCulture)
            : GetString("Common.Unavailable");
    public string TargetDiskSelectionHint => !string.IsNullOrWhiteSpace(SelectedTargetDisk?.SelectionWarning)
        ? SelectedTargetDisk.SelectionWarning
        : GetString("Preparation.TargetDiskHint");
    public string AutopilotProfileHint =>
        !IsJsonProfileMode
            ? string.Empty
            : HasAutopilotProfiles
            ? string.Empty
            : IsAutopilotEnabled
                ? GetString("Preparation.AutopilotProfilesMissing")
                : string.Empty;
    public bool HasAutopilotProfileHint => !string.IsNullOrWhiteSpace(AutopilotProfileHint);
    public string AutopilotModeText => AutopilotProvisioningMode switch
    {
        AutopilotProvisioningMode.HardwareHashUpload => GetString("Preparation.AutopilotModeHardwareHashUpload"),
        AutopilotProvisioningMode.InteractiveHardwareHashUpload => GetString("Preparation.AutopilotModeInteractiveHardwareHashUpload"),
        _ => GetString("Preparation.AutopilotModeJsonProfile")
    };
    public string AutopilotHardwareHashUploadStatusText
    {
        get
        {
            if (IsHardwareHashCertificateUsable)
            {
                return GetString("Preparation.AutopilotHardwareHashReadyStatus");
            }

            return GetString("Preparation.AutopilotHardwareHashUnavailableStatus");
        }
    }

    public string AutopilotHardwareHashUploadMessage
    {
        get
        {
            if (IsHardwareHashCertificateUsable)
            {
                return string.Empty;
            }

            if (IsHardwareHashCertificateExpired)
            {
                return GetString("Preparation.AutopilotHardwareHashExpiredMessage");
            }

            return GetString("Preparation.AutopilotHardwareHashMissingMetadataMessage");
        }
    }

    public string EffectiveHardwareHashGroupTagText => string.IsNullOrWhiteSpace(ResolveEffectiveHardwareHashGroupTag())
        ? GetString("Common.None")
        : ResolveEffectiveHardwareHashGroupTag()!;

    public bool HasTargetComputerNameValidationError => !UsesCustomUnattend && !string.IsNullOrWhiteSpace(TargetComputerNameValidationMessage);
    public bool IsTargetComputerNameValid => UsesCustomUnattend || (!HasTargetComputerNameValidationError && ComputerNameRules.IsValid(TargetComputerName));

    public HardwareProfile? DetectedHardware => _detectedHardware;
    public string HardwareManufacturerText => GetHardwareValue(_detectedHardware?.Manufacturer);
    public string HardwareModelText => GetHardwareValue(_detectedHardware?.Model);
    public string HardwareProductText => GetHardwareValue(_detectedHardware?.Product);
    public string HardwareSerialNumberText => GetHardwareValue(_detectedHardware?.SerialNumber);
    public string HardwareArchitectureText => string.IsNullOrWhiteSpace(_detectedHardware?.Architecture)
        ? GetString("Common.Unavailable")
        : _detectedHardware.Architecture;
    public string HardwareTpmText => _detectedHardware is null
        ? GetString("Common.Unavailable")
        : _detectedHardware.IsTpmPresent
            ? GetString("Common.Yes")
            : GetString("Common.No");
    public string HardwarePowerText => _detectedHardware is null
        ? GetString("Common.Unavailable")
        : _detectedHardware.IsOnBattery
            ? GetString("Preparation.PowerBattery")
            : GetString("Preparation.PowerAc");
    public string HardwareFirmwareText => !string.IsNullOrWhiteSpace(_detectedHardware?.SystemFirmwareHardwareId)
        ? GetString("Common.Detected")
        : GetString("Common.Unavailable");

    /// <summary>
    /// Builds the hardware hash upload settings to carry into a deployment launch request.
    /// </summary>
    /// <returns>The configured hardware hash upload metadata with the user-selected group tag override applied.</returns>
    public DeployAutopilotHardwareHashUploadSettings CreateAutopilotHardwareHashUploadForLaunch()
    {
        if (!IsAutopilotEnabled || !IsHardwareHashUploadMode)
        {
            return new DeployAutopilotHardwareHashUploadSettings();
        }

        return AutopilotHardwareHashUpload with
        {
            DefaultGroupTag = ResolveEffectiveHardwareHashGroupTag()
        };
    }

    public void ApplyMachineNamingConfiguration(DeployMachineNamingSettings settings)
    {
        _machineNamingConfiguration = settings ?? new DeployMachineNamingSettings();
        IsTargetComputerNameReadOnly = _machineNamingConfiguration.IsEnabled &&
                                       _machineNamingConfiguration.Mode == Foundry.Core.Models.Configuration.MachineNamingMode.Composed &&
                                       !_machineNamingConfiguration.AllowEditingDuringDeployment;
        OnPropertyChanged(nameof(IsComputerNameInputReadOnly));

        RaiseStateChanged();
    }

    public void ApplyAutopilotConfiguration(
        DeployAutopilotSettings settings,
        IReadOnlyList<AutopilotProfileCatalogItem> profiles)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profiles);

        AutopilotProfiles.Clear();
        foreach (AutopilotProfileCatalogItem profile in profiles)
        {
            AutopilotProfiles.Add(profile);
        }

        OnPropertyChanged(nameof(HasAutopilotProfiles));
        OnPropertyChanged(nameof(IsAutopilotProfileSelectionEnabled));
        OnPropertyChanged(nameof(AutopilotProfileHint));
        OnPropertyChanged(nameof(HasAutopilotProfileHint));

        AutopilotProvisioningMode = settings.ProvisioningMode;
        AutopilotHardwareHashUpload = settings.HardwareHashUpload ?? new DeployAutopilotHardwareHashUploadSettings();
        SelectedAutopilotProfile = settings.IsEnabled && settings.ProvisioningMode == AutopilotProvisioningMode.JsonProfile
            ? ResolveDefaultAutopilotProfile(settings.DefaultProfileFolderName)
            : null;
        IsAutopilotEnabled = settings.IsEnabled;

        RaiseStateChanged();
    }

    public void SetDetectedHardware(HardwareProfile? profile)
    {
        _detectedHardware = profile;

        if (profile is null)
        {
            _detectedHardwareSummaryRaw = "Hardware detection failed.";
            DetectedHardwareSummary = DeploymentUiTextLocalizer.LocalizeMessage(_detectedHardwareSummaryRaw);
            OnPropertyChanged(nameof(IsFirmwareUpdatesOptionEnabled));
            RaiseHardwareInventoryPropertiesChanged();
            RaiseStateChanged();
            return;
        }

        SyncFirmwareOptionFromHardware(profile);
        _detectedHardwareSummaryRaw = Format(
            "Preparation.HardwareSummaryFormat",
            profile.DisplayLabel,
            profile.IsTpmPresent ? GetString("Common.Yes") : GetString("Common.No"),
            profile.IsOnBattery ? GetString("Preparation.PowerBattery") : GetString("Preparation.PowerAc"),
            profile.SystemFirmwareHardwareId.Length > 0 ? GetString("Common.Detected") : GetString("Common.Unavailable"));
        DetectedHardwareSummary = _detectedHardwareSummaryRaw;
        OnPropertyChanged(nameof(IsFirmwareUpdatesOptionEnabled));
        RaiseHardwareInventoryPropertiesChanged();
        RaiseStateChanged();
    }

    public void SetHardwareDetectionFailure(string message)
    {
        _detectedHardware = null;
        _detectedHardwareSummaryRaw = message;
        DetectedHardwareSummary = DeploymentUiTextLocalizer.LocalizeMessage(message);
        OnPropertyChanged(nameof(IsFirmwareUpdatesOptionEnabled));
        RaiseHardwareInventoryPropertiesChanged();
        RaiseStateChanged();
    }

    public void ApplyOfflineComputerName(string effectiveName)
    {
        if (!string.IsNullOrEmpty(TargetComputerName))
        {
            return;
        }

        ApplyComputerName(effectiveName);
        RaiseStateChanged();
    }

    public void ApplyMachineNamePreparation(MachineNamePreparationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _machineNamePreparationFailure = result.IsSuccess ? null : result;
        ApplyComputerName(result.ComputerName);
    }

    public void ApplyTargetDisks(IReadOnlyList<TargetDiskInfo> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);

        TargetDisks.Clear();
        foreach (TargetDiskInfo disk in disks)
        {
            TargetDisks.Add(disk);
        }

        if (_isDebugSafeMode && !TargetDisks.Any(item => item.IsSelectable))
        {
            TargetDisks.Insert(0, TargetDiskInfoFactory.CreateDebugVirtualDisk());
        }

        if (TargetDisks.Count == 0)
        {
            SelectedTargetDisk = null;
            RaiseStateChanged();
            return;
        }

        TargetDiskInfo? currentSelection = SelectedTargetDisk is null
            ? null
            : TargetDisks.FirstOrDefault(item => item.DiskNumber == SelectedTargetDisk.DiskNumber);

        SelectedTargetDisk = currentSelection
            ?? TargetDisks.FirstOrDefault(item => item.IsSelectable)
            ?? (_isDebugSafeMode ? TargetDisks.FirstOrDefault(item => item.DiskNumber == TargetDiskInfoFactory.CreateDebugVirtualDisk().DiskNumber) : null)
            ?? TargetDisks.FirstOrDefault();

        RaiseStateChanged();
    }

    partial void OnTargetComputerNameChanged(string value)
    {
        OnPropertyChanged(nameof(EffectiveComputerName));
        if (_isApplyingComputerName)
        {
            RaiseStateChanged();
            return;
        }

        _machineNamePreparationFailure = null;
        ApplyComputerName(value);
    }

    partial void OnApplyFirmwareUpdatesChanged(bool value)
    {
        if (_isUpdatingFirmwareOptionSelection)
        {
            RaiseStateChanged();
            return;
        }

        _hasUserSelectedFirmwareOption = true;
        _firmwareUpdatesPreference = value;
        RaiseStateChanged();
    }

    partial void OnIsAutopilotEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsJsonProfileControlsVisible));
        OnPropertyChanged(nameof(IsHardwareHashUploadControlsVisible));
        OnPropertyChanged(nameof(IsAutopilotProfileSelectionEnabled));
        OnPropertyChanged(nameof(AutopilotProfileHint));
        OnPropertyChanged(nameof(HasAutopilotProfileHint));
        RaiseHardwareHashPropertiesChanged();
        RaiseStateChanged();
    }

    /// <summary>
    /// Applies an in-memory Autopilot mode override for debug safe mode without changing the persisted deployment configuration.
    /// </summary>
    /// <param name="mode">Debug Autopilot mode to apply.</param>
    public void ApplyDebugAutopilotMode(DebugAutopilotMode mode)
    {
        switch (mode)
        {
            case DebugAutopilotMode.None:
                IsAutopilotEnabled = false;
                AutopilotProvisioningMode = AutopilotProvisioningMode.JsonProfile;
                SelectedAutopilotProfile = null;
                break;
            case DebugAutopilotMode.JsonProfile:
                EnsureDebugAutopilotProfile();
                AutopilotProvisioningMode = AutopilotProvisioningMode.JsonProfile;
                SelectedAutopilotProfile = AutopilotProfiles.First(profile =>
                    profile.FolderName.Equals(DebugAutopilotProfileFolderName, StringComparison.OrdinalIgnoreCase));
                IsAutopilotEnabled = true;
                break;
            case DebugAutopilotMode.HardwareHashUploadValidCertificate:
                ApplyDebugHardwareHashUpload(
                    certificateExpiresOnUtc: DateTimeOffset.UtcNow.AddMonths(1),
                    defaultGroupTag: DebugAutopilotGroupTag,
                    includeCompleteCertificateMetadata: true);
                break;
            case DebugAutopilotMode.HardwareHashUploadExpiredCertificate:
                ApplyDebugHardwareHashUpload(
                    certificateExpiresOnUtc: DateTimeOffset.UtcNow.AddDays(-1),
                    defaultGroupTag: DebugAutopilotGroupTag,
                    includeCompleteCertificateMetadata: true);
                break;
            case DebugAutopilotMode.HardwareHashUploadMissingCertificateMetadata:
                ApplyDebugHardwareHashUpload(
                    certificateExpiresOnUtc: DateTimeOffset.UtcNow.AddMonths(1),
                    defaultGroupTag: DebugAutopilotGroupTag,
                    includeCompleteCertificateMetadata: false);
                break;
            case DebugAutopilotMode.HardwareHashUploadNoDefaultGroupTag:
                ApplyDebugHardwareHashUpload(
                    certificateExpiresOnUtc: DateTimeOffset.UtcNow.AddMonths(1),
                    defaultGroupTag: null,
                    includeCompleteCertificateMetadata: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported debug Autopilot mode.");
        }

        RaiseStateChanged();
    }

    private void ApplyDebugHardwareHashUpload(DateTimeOffset certificateExpiresOnUtc, string? defaultGroupTag, bool includeCompleteCertificateMetadata)
    {
        AutopilotProvisioningMode = AutopilotProvisioningMode.HardwareHashUpload;
        AutopilotHardwareHashUpload = new DeployAutopilotHardwareHashUploadSettings
        {
            TenantId = "debug-tenant-id",
            ClientId = "debug-client-id",
            ActiveCertificateKeyId = includeCompleteCertificateMetadata ? "debug-certificate-key-id" : null,
            ActiveCertificateThumbprint = includeCompleteCertificateMetadata ? "DEBUGTHUMBPRINT" : null,
            ActiveCertificateExpiresOnUtc = includeCompleteCertificateMetadata ? certificateExpiresOnUtc : null,
            DefaultGroupTag = defaultGroupTag,
            KnownGroupTags = [DebugAutopilotGroupTag, "Kiosk"]
        };
        SelectedAutopilotProfile = null;
        IsAutopilotEnabled = true;
    }

    partial void OnAutopilotProvisioningModeChanged(AutopilotProvisioningMode value)
    {
        OnPropertyChanged(nameof(IsJsonProfileMode));
        OnPropertyChanged(nameof(IsHardwareHashUploadMode));
        OnPropertyChanged(nameof(IsJsonProfileControlsVisible));
        OnPropertyChanged(nameof(IsHardwareHashUploadControlsVisible));
        OnPropertyChanged(nameof(IsAutopilotProfileSelectionEnabled));
        OnPropertyChanged(nameof(AutopilotProfileHint));
        OnPropertyChanged(nameof(HasAutopilotProfileHint));
        OnPropertyChanged(nameof(AutopilotModeText));
        RaiseHardwareHashPropertiesChanged();
        RaiseStateChanged();
    }

    partial void OnAutopilotHardwareHashUploadChanged(DeployAutopilotHardwareHashUploadSettings value)
    {
        SelectedHardwareHashGroupTag = null;
        RefreshHardwareHashGroupTagOptions();
        RaiseHardwareHashPropertiesChanged();
        RaiseStateChanged();
    }

    partial void OnSelectedHardwareHashGroupTagChanged(HardwareHashGroupTagOption? value)
    {
        OnPropertyChanged(nameof(EffectiveHardwareHashGroupTagText));
        RaiseStateChanged();
    }

    partial void OnSelectedAutopilotProfileChanged(AutopilotProfileCatalogItem? value)
    {
        OnPropertyChanged(nameof(IsAutopilotProfileSelectionEnabled));
        RaiseStateChanged();
    }

    partial void OnSelectedTargetDiskChanged(TargetDiskInfo? value)
    {
        OnPropertyChanged(nameof(TargetDiskSelectionHint));
        RaiseStateChanged();
    }

    private void ApplyComputerName(string? value)
    {
        string computerName = ComputerNameRules.Normalize(value);

        _isApplyingComputerName = true;
        try
        {
            TargetComputerName = computerName;
            TargetComputerNameValidationMessage = ResolveComputerNameValidationMessage(computerName);
        }
        finally
        {
            _isApplyingComputerName = false;
        }

        RaiseStateChanged();
    }

    private void SyncFirmwareOptionFromHardware(HardwareProfile profile)
    {
        bool desiredValue = profile.IsVirtualMachine
            ? false
            : _hasUserSelectedFirmwareOption
                ? _firmwareUpdatesPreference
                : true;

        _isUpdatingFirmwareOptionSelection = true;
        try
        {
            ApplyFirmwareUpdates = desiredValue;
        }
        finally
        {
            _isUpdatingFirmwareOptionSelection = false;
        }
    }

    private AutopilotProfileCatalogItem? ResolveDefaultAutopilotProfile(string? defaultProfileFolderName)
    {
        if (!string.IsNullOrWhiteSpace(defaultProfileFolderName))
        {
            AutopilotProfileCatalogItem? matchingProfile = AutopilotProfiles.FirstOrDefault(profile =>
                profile.FolderName.Equals(defaultProfileFolderName, StringComparison.OrdinalIgnoreCase));
            if (matchingProfile is not null)
            {
                return matchingProfile;
            }
        }

        return null;
    }

    private void EnsureDebugAutopilotProfile()
    {
        if (AutopilotProfiles.Any(profile =>
                profile.FolderName.Equals(DebugAutopilotProfileFolderName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        AutopilotProfiles.Add(new AutopilotProfileCatalogItem
        {
            FolderName = DebugAutopilotProfileFolderName,
            DisplayName = DebugAutopilotProfileDisplayName,
            ConfigurationFilePath = @"X:\Foundry\Debug\Autopilot\AutopilotConfigurationFile.json"
        });
        OnPropertyChanged(nameof(HasAutopilotProfiles));
        OnPropertyChanged(nameof(IsAutopilotProfileSelectionEnabled));
        OnPropertyChanged(nameof(AutopilotProfileHint));
        OnPropertyChanged(nameof(HasAutopilotProfileHint));
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void Dispose()
    {
        LocalizationService.LanguageChanged -= OnLocalizationLanguageChanged;
        base.Dispose();
    }

    private void OnLocalizationLanguageChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (_detectedHardware is not null)
            {
                SetDetectedHardware(_detectedHardware);
            }
            else
            {
                DetectedHardwareSummary = DeploymentUiTextLocalizer.LocalizeMessage(_detectedHardwareSummaryRaw);
                RaiseHardwareInventoryPropertiesChanged();
            }

            TargetComputerNameValidationMessage = ResolveComputerNameValidationMessage(TargetComputerName);
            OnPropertyChanged(nameof(AutopilotProfileHint));
            OnPropertyChanged(nameof(HasAutopilotProfileHint));
            OnPropertyChanged(nameof(AutopilotModeText));
            RefreshHardwareHashGroupTagOptions();
            RefreshUnattendLocalization();
            RaiseHardwareHashPropertiesChanged();
            OnPropertyChanged(nameof(TargetDiskSelectionHint));
            OnPropertyChanged(nameof(TargetDisks));
            OnPropertyChanged(nameof(SelectedTargetDisk));
        });
    }

    private void RaiseHardwareInventoryPropertiesChanged()
    {
        OnPropertyChanged(nameof(HardwareManufacturerText));
        OnPropertyChanged(nameof(HardwareModelText));
        OnPropertyChanged(nameof(HardwareProductText));
        OnPropertyChanged(nameof(HardwareSerialNumberText));
        OnPropertyChanged(nameof(HardwareArchitectureText));
        OnPropertyChanged(nameof(HardwareTpmText));
        OnPropertyChanged(nameof(HardwarePowerText));
        OnPropertyChanged(nameof(HardwareFirmwareText));
    }

    private string GetHardwareValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? GetString("Common.Unavailable") : value;
    }

    private string ResolveComputerNameValidationMessage(string? value)
    {
        if (ComputerNameRules.IsValid(value))
        {
            return string.Empty;
        }

        return _machineNamePreparationFailure?.ComponentType is { } componentType
            ? $"{GetMachineNameComponentDisplayName(componentType)}: {GetString("Common.Unavailable")}"
            : GetString("Preparation.ComputerNameValidationMessage");
    }

    private string GetMachineNameComponentDisplayName(Foundry.Core.Models.Configuration.MachineNameComponentType type) =>
        type switch
        {
            Foundry.Core.Models.Configuration.MachineNameComponentType.SerialNumber => GetString("TargetDevice.SerialNumber"),
            Foundry.Core.Models.Configuration.MachineNameComponentType.Manufacturer => GetString("TargetDevice.Manufacturer"),
            Foundry.Core.Models.Configuration.MachineNameComponentType.Model => GetString("TargetDevice.Model"),
            Foundry.Core.Models.Configuration.MachineNameComponentType.AssetTag => GetString("TargetDevice.AssetTag"),
            Foundry.Core.Models.Configuration.MachineNameComponentType.SystemUuid => GetString("TargetDevice.SystemUuid"),
            _ => GetString("Preparation.ComputerName")
        };

    private string GetString(string key)
    {
        return Strings[key];
    }

    private string Format(string key, params object[] args)
    {
        return string.Format(LocalizationService.CurrentCulture, GetString(key), args);
    }

    private string? ResolveEffectiveHardwareHashGroupTag()
    {
        return string.IsNullOrWhiteSpace(SelectedHardwareHashGroupTag?.GroupTag)
            ? null
            : SelectedHardwareHashGroupTag.GroupTag.Trim();
    }

    private void RefreshHardwareHashGroupTagOptions()
    {
        string? preferredGroupTag = SelectedHardwareHashGroupTag is not null
            ? SelectedHardwareHashGroupTag.GroupTag
            : AutopilotHardwareHashUpload.DefaultGroupTag;

        HardwareHashGroupTagOptions.Clear();
        HardwareHashGroupTagOptions.Add(new HardwareHashGroupTagOption(GetString("Common.None"), null));

        foreach (string groupTag in AutopilotHardwareHashUpload.KnownGroupTags
                     .Select(static value => value.Trim())
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            HardwareHashGroupTagOptions.Add(new HardwareHashGroupTagOption(groupTag, groupTag));
        }

        SelectedHardwareHashGroupTag = HardwareHashGroupTagOptions.FirstOrDefault(option =>
            !string.IsNullOrWhiteSpace(option.GroupTag) &&
            string.Equals(option.GroupTag, preferredGroupTag, StringComparison.OrdinalIgnoreCase))
            ?? HardwareHashGroupTagOptions[0];

        OnPropertyChanged(nameof(HardwareHashGroupTagOptions));
        OnPropertyChanged(nameof(EffectiveHardwareHashGroupTagText));
    }

    private string NormalizeMetadataValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? GetString("Common.Unavailable")
            : value.Trim();
    }

    private void RaiseHardwareHashPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasHardwareHashUploadMetadata));
        OnPropertyChanged(nameof(IsHardwareHashCertificateExpired));
        OnPropertyChanged(nameof(IsHardwareHashCertificateUsable));
        OnPropertyChanged(nameof(IsHardwareHashGroupTagControlsVisible));
        OnPropertyChanged(nameof(IsHardwareHashUploadMessageVisible));
        OnPropertyChanged(nameof(AutopilotHardwareHashTenantIdText));
        OnPropertyChanged(nameof(AutopilotHardwareHashCertificateExpirationText));
        OnPropertyChanged(nameof(AutopilotHardwareHashUploadStatusText));
        OnPropertyChanged(nameof(AutopilotHardwareHashUploadMessage));
        OnPropertyChanged(nameof(EffectiveHardwareHashGroupTagText));
    }

}
