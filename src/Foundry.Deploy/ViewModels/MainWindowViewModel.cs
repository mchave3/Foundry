// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Deploy;
using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Operations;
using Foundry.Deploy.Services.Runtime;
using Foundry.Deploy.Services.Security;
using Foundry.Deploy.Services.Startup;
using Foundry.Deploy.Services.ApplicationShell;
using Foundry.Deploy.Services.Localization;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.Networking;
using Foundry.Deploy.Services.Theme;
using Foundry.Deploy.Services.Wizard;
using Foundry.Localization;
using ComputerNameRules = Foundry.Core.Services.Configuration.ComputerNameRules;
using Microsoft.Extensions.Logging;
using DeployThemeMode = Foundry.Deploy.Services.Theme.ThemeMode;

namespace Foundry.Deploy.ViewModels;

public partial class MainWindowViewModel : LocalizedViewModelBase
{
    private readonly IThemeService _themeService;
    private readonly IDeploymentStartupCoordinator _deploymentStartupCoordinator;
    private readonly IDeploymentAccessGate _deploymentAccessGate;
    private readonly IDeploymentLaunchPreparationService _deploymentLaunchPreparationService;
    private readonly IDeploymentExecutionService _deploymentExecutionService;
    private readonly IDeploymentWizardStateService _deploymentWizardStateService;
    private readonly IDeploymentOrchestrator _deploymentOrchestrator;
    private readonly IApplicationShellService _applicationShellService;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DeploymentRuntimeContext _deploymentRuntimeContext;
    private readonly DeploymentWizardContext _wizardContext;
    private readonly DeploymentWizardNavigationState _wizardNavigationState;
    private DebugAutopilotMode _debugAutopilotMode = DebugAutopilotMode.None;
    private bool _isInitialized;
    private bool _isDisposed;
    private Task? _initializationTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousWizardStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextWizardStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartDeploymentCommand))]
    private int wizardStepIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextWizardStepCommand))]
    private bool isCatalogLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousWizardStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextWizardStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartDeploymentCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowDebugProgressPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowDebugSuccessPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowDebugErrorPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetDebugAutopilotModeCommand))]
    private bool isDeploymentRunning;

    [ObservableProperty]
    private bool isBootMediaUpdateRecommended;

    public DeploymentPreparationViewModel Preparation { get; }
    public DeploymentSessionViewModel Session { get; }
    public OperatingSystemCatalogViewModel OperatingSystemCatalog { get; }
    public DriverPackSelectionViewModel DriverPackSelection { get; }
    public ObservableCollection<SupportedCultureOption> SupportedCultures { get; } = [];
    public ObservableCollection<DeploymentWizardStepViewModel> WizardSteps { get; } = [];
    public ObservableCollection<DeploymentSummaryCategoryViewModel> SummaryCategories { get; } = [];

    public CultureInfo CurrentCulture => LocalizationService.CurrentCulture;
    public DeployThemeMode CurrentTheme => _themeService.CurrentTheme;
    public bool IsDebugSafeMode => DebugSafetyMode.IsEnabled;
    public string EffectiveOsArchitecture => OperatingSystemCatalog.EffectiveOsArchitecture;
    public OperatingSystemCatalogItem? SelectedOperatingSystem => OperatingSystemCatalog.SelectedOperatingSystem;
    public string WindowTitle => GetString("App.WindowTitle");
    public string VersionDisplay => Format("Common.VersionFormat", FoundryDeployApplicationInfo.Version);
    public string BootMediaUpdateRecommendedText => GetString("BootMedia.UpdateRecommended");
    public string BootMediaUpdateRecommendedToolTip => GetString("BootMedia.UpdateRecommendedToolTip");
    public string OperatingSystemArchitectureDisplay => Format("Catalog.ArchitectureFormat", OperatingSystemCatalog.EffectiveOsArchitecture);
    public string SummaryTargetDiskText => Preparation.SelectedTargetDisk?.DisplayLabel ?? GetString("Summary.NoDiskSelected");
    public string SummaryOperatingSystemText => SelectedOperatingSystem is null
        ? GetString("Summary.NoSelection")
        : new Converters.OperatingSystemSummaryConverter().Convert(SelectedOperatingSystem, typeof(string), string.Empty, LocalizationService.CurrentCulture)?.ToString() ?? GetString("Summary.NoSelection");
    public string SummaryFirmwareText => Preparation.ApplyFirmwareUpdates ? GetString("Common.Enabled") : GetString("Common.Disabled");
    public string SummaryAutopilotProfileText => Preparation.SelectedAutopilotProfile?.DisplayName ?? GetString("Common.None");
    public string SummaryAutopilotGroupTagText => Preparation.EffectiveHardwareHashGroupTagText;
    public DeploymentWizardStepId CurrentWizardStepId => WizardSteps[WizardStepIndex].Id;
    public bool IsOperatingSystemStep => CurrentWizardStepId == DeploymentWizardStepId.OperatingSystem;
    public bool IsDriversStep => CurrentWizardStepId == DeploymentWizardStepId.Drivers;
    public bool IsAutopilotStep => CurrentWizardStepId == DeploymentWizardStepId.Autopilot;
    public bool IsSummaryStep => CurrentWizardStepId == DeploymentWizardStepId.Summary;
    public bool IsReturningToSummary => _wizardNavigationState.IsReturningToSummary;
    public bool IsDebugAutopilotNoneMode => IsDebugAutopilotMode(DebugAutopilotMode.None);
    public bool IsDebugAutopilotJsonProfileMode => IsDebugAutopilotMode(DebugAutopilotMode.JsonProfile);
    public bool IsDebugAutopilotHardwareHashUploadValidCertificateMode => IsDebugAutopilotMode(DebugAutopilotMode.HardwareHashUploadValidCertificate);
    public bool IsDebugAutopilotHardwareHashUploadExpiredCertificateMode => IsDebugAutopilotMode(DebugAutopilotMode.HardwareHashUploadExpiredCertificate);
    public bool IsDebugAutopilotHardwareHashUploadMissingCertificateMetadataMode => IsDebugAutopilotMode(DebugAutopilotMode.HardwareHashUploadMissingCertificateMetadata);
    public bool IsDebugAutopilotHardwareHashUploadNoDefaultGroupTagMode => IsDebugAutopilotMode(DebugAutopilotMode.HardwareHashUploadNoDefaultGroupTag);

    public MainWindowViewModel(
        ILocalizationService localizationService,
        IThemeService themeService,
        IOperationProgressService operationProgressService,
        IDeploymentAccessGate deploymentAccessGate,
        IDeploymentStartupCoordinator deploymentStartupCoordinator,
        IDeploymentRuntimeContextService deploymentRuntimeContextService,
        IDeploymentLaunchPreparationService deploymentLaunchPreparationService,
        IDeploymentExecutionService deploymentExecutionService,
        IDeploymentWizardStateService deploymentWizardStateService,
        IDeploymentOrchestrator deploymentOrchestrator,
        IApplicationShellService applicationShellService,
        IDeploymentWizardContextFactory deploymentWizardContextFactory,
        IProcessRunner processRunner,
        ILogger<MainWindowViewModel> logger,
        INetworkAdapterSnapshotProvider? networkAdapterSnapshotProvider = null)
        : base(localizationService)
    {
        _themeService = themeService;
        _deploymentAccessGate = deploymentAccessGate;
        _deploymentStartupCoordinator = deploymentStartupCoordinator;
        _deploymentLaunchPreparationService = deploymentLaunchPreparationService;
        _deploymentExecutionService = deploymentExecutionService;
        _deploymentWizardStateService = deploymentWizardStateService;
        _deploymentOrchestrator = deploymentOrchestrator;
        _applicationShellService = applicationShellService;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _deploymentRuntimeContext = deploymentRuntimeContextService.Resolve();
        _wizardContext = deploymentWizardContextFactory.Create(IsDebugSafeMode);
        _wizardContext.StateChanged += OnWizardContextStateChanged;
        Preparation = _wizardContext.Preparation;
        OperatingSystemCatalog = _wizardContext.OperatingSystemCatalog;
        DriverPackSelection = _wizardContext.DriverPackSelection;
        _wizardNavigationState = new DeploymentWizardNavigationState(
            DeploymentWizardStepDefinition.CreateSequence(HasAutopilotConfigurationStep()));
        RefreshWizardSteps();
        RefreshSummaryCategories();
        Session = new DeploymentSessionViewModel(
            _dispatcher,
            _logger,
            operationProgressService,
            _deploymentOrchestrator,
            processRunner,
            localizationService,
            IsDebugSafeMode,
            networkAdapterSnapshotProvider);
        Session.PropertyChanged += OnSessionPropertyChanged;
        LocalizationService.LanguageChanged += OnLocalizationLanguageChanged;
        RefreshSupportedCultures();
    }

    public Task InitializeAsync()
    {
        return _initializationTask ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        if (!await _deploymentAccessGate.AuthorizeAsync())
        {
            _applicationShellService.Shutdown();
            return;
        }

        DeploymentStartupSnapshot startupSnapshot = await _deploymentStartupCoordinator.InitializeAsync(
                new DeploymentStartupRequest
                {
                    RuntimeContext = _deploymentRuntimeContext,
                    IsDebugSafeMode = IsDebugSafeMode,
                    FallbackComputerName = ResolveInitialComputerName()
                })
            .ConfigureAwait(false);

        RunOnUi(() => ApplyStartupSnapshot(startupSnapshot));

        _isInitialized = true;
    }

    [RelayCommand]
    private void SetCulture(string cultureName)
    {
        LocalizationService.SetCulture(new CultureInfo(cultureName));
    }

    private void RefreshSupportedCultures()
    {
        SupportedCultures.Clear();
        foreach (SupportedCultureOption option in LocalizationService.CreateSupportedCultureOptions())
        {
            SupportedCultures.Add(option);
        }
    }

    [RelayCommand]
    private void SetSystemTheme()
    {
        _themeService.SetTheme(DeployThemeMode.System);
        OnPropertyChanged(nameof(CurrentTheme));
    }

    [RelayCommand]
    private void SetLightTheme()
    {
        _themeService.SetTheme(DeployThemeMode.Light);
        OnPropertyChanged(nameof(CurrentTheme));
    }

    [RelayCommand]
    private void SetDarkTheme()
    {
        _themeService.SetTheme(DeployThemeMode.Dark);
        OnPropertyChanged(nameof(CurrentTheme));
    }

    [RelayCommand]
    private void ShowAbout()
    {
        _applicationShellService.ShowAbout();
    }

    [RelayCommand(CanExecute = nameof(CanUseDebugTools))]
    private void SetDebugAutopilotMode(DebugAutopilotMode mode)
    {
        if (!IsDebugSafeMode)
        {
            return;
        }

        _debugAutopilotMode = mode;
        Preparation.ApplyDebugAutopilotMode(mode);
        RaiseDebugAutopilotModePropertiesChanged();
        OnPropertyChanged(nameof(SummaryAutopilotProfileText));
        OnPropertyChanged(nameof(SummaryAutopilotGroupTagText));
        NextWizardStepCommand.NotifyCanExecuteChanged();
        StartDeploymentCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousWizardStep()
    {
        if (_wizardNavigationState.MovePrevious())
        {
            SynchronizeWizardNavigation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextWizardStep()
    {
        if (_wizardNavigationState.Advance())
        {
            SynchronizeWizardNavigation();
        }
    }

    [RelayCommand]
    private void NavigateToWizardStep(DeploymentWizardStepViewModel? step)
    {
        if (step is not null && _wizardNavigationState.TryNavigateTo(step.Id))
        {
            SynchronizeWizardNavigation();
        }
    }

    [RelayCommand]
    private void EditSummaryStep(DeploymentWizardStepId stepId)
    {
        if (_wizardNavigationState.BeginSummaryEdit(stepId))
        {
            SynchronizeWizardNavigation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBeginWizard))]
    private void BeginWizard()
    {
        Session.ShowWizard();
    }

    [RelayCommand(CanExecute = nameof(CanStartDeployment))]
    private async Task StartDeploymentAsync()
    {
        _logger.LogInformation("Start deployment requested.");
        DriverPackSelectionKind effectiveDriverPackKind = DriverPackSelection.EffectiveSelectionKind;
        DriverPackCatalogItem? effectiveDriverPack = DriverPackSelection.ResolveEffectiveSelection();
        DeploymentLaunchPreparationResult launchPreparation = _deploymentLaunchPreparationService.Prepare(
            new DeploymentLaunchRequest
            {
                Mode = _deploymentRuntimeContext.Mode,
                CacheRootPath = Preparation.CacheRootPath,
                Unattend = Preparation.SelectedUnattend,
                TargetComputerName = Preparation.TargetComputerName,
                DefaultTimeZoneId = _wizardContext.DefaultTimeZoneId,
                SelectedTargetDisk = Preparation.SelectedTargetDisk,
                SelectedOperatingSystem = OperatingSystemCatalog.SelectedOperatingSystem,
                DriverPackSelectionKind = effectiveDriverPackKind,
                SelectedDriverPack = effectiveDriverPack,
                ApplyFirmwareUpdates = Preparation.ApplyFirmwareUpdates,
                IsAutopilotEnabled = Preparation.IsAutopilotEnabled,
                AutopilotProvisioningMode = Preparation.AutopilotProvisioningMode,
                SelectedAutopilotProfile = Preparation.SelectedAutopilotProfile,
                AutopilotHardwareHashUpload = Preparation.CreateAutopilotHardwareHashUploadForLaunch(),
                Network = _wizardContext.Network,
                Oobe = _wizardContext.Oobe,
                AppxRemoval = _wizardContext.AppxRemoval,
                AiComponentRemoval = _wizardContext.AiComponentRemoval,
                WindowsOptionalFeatures = _wizardContext.WindowsOptionalFeatures,
                Completion = _wizardContext.Completion,
                IsDryRun = IsDebugSafeMode
            });

        if (!string.Equals(Preparation.TargetComputerName, launchPreparation.NormalizedComputerName, StringComparison.Ordinal))
        {
            Preparation.TargetComputerName = launchPreparation.NormalizedComputerName;
        }

        if (!launchPreparation.IsReadyToStart || launchPreparation.Context is null)
        {
            if (launchPreparation.FailureMessage is not null)
                Preparation.ReportUnattendFailure(launchPreparation.FailureMessage);
            return;
        }

        RunOnUi(() =>
        {
            IsDeploymentRunning = true;
            Session.BeginDeployment(Preparation.EffectiveComputerName, _deploymentOrchestrator.PlannedSteps.Count);
        });

        try
        {
            DeploymentExecutionRunResult executionRunResult = await _deploymentExecutionService
                .ExecuteAsync(launchPreparation.Context)
                .ConfigureAwait(false);

            RunOnUi(() => Session.ApplyExecutionRunResult(executionRunResult));
        }
        finally
        {
            RunOnUi(() =>
            {
                IsDeploymentRunning = false;
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanShowDebugPages))]
    private void ShowDebugProgressPage()
    {
        Session.ShowDebugProgress(
            Preparation.EffectiveComputerName,
            currentStepIndex: 7,
            plannedStepCount: _deploymentOrchestrator.PlannedSteps.Count,
            currentStepName: DeploymentStepNames.ApplyOperatingSystemImage,
            progressPercent: 42);
    }

    [RelayCommand(CanExecute = nameof(CanShowDebugPages))]
    private void ShowDebugSuccessPage()
    {
        Session.ShowDebugSuccess(
            Preparation.EffectiveComputerName,
            _deploymentOrchestrator.PlannedSteps.Count,
            DeploymentStepNames.FinalizeDeploymentAndWriteLogs);
    }

    [RelayCommand(CanExecute = nameof(CanShowDebugPages))]
    private void ShowDebugErrorPage()
    {
        Session.ShowDebugError(
            Preparation.EffectiveComputerName,
            currentStepIndex: 7,
            failedStepName: DeploymentStepNames.ApplyOperatingSystemImage,
            failedStepErrorMessage:
            "Debug preview: DISM apply failed because the target partition is read-only.\n\n" +
            "ErrorCode=0x80070005\n" +
            "Details: Access denied while mounting image to target path.\n" +
            "Action: Verify disk attributes and retry deployment.");
    }

    private void OnWizardContextStateChanged(object? sender, EventArgs e)
    {
        RefreshWizardSteps();
        RefreshSummaryCategories();
        OnPropertyChanged(nameof(EffectiveOsArchitecture));
        OnPropertyChanged(nameof(SelectedOperatingSystem));
        OnPropertyChanged(nameof(OperatingSystemArchitectureDisplay));
        OnPropertyChanged(nameof(SummaryTargetDiskText));
        OnPropertyChanged(nameof(SummaryOperatingSystemText));
        OnPropertyChanged(nameof(SummaryFirmwareText));
        OnPropertyChanged(nameof(SummaryAutopilotProfileText));
        OnPropertyChanged(nameof(SummaryAutopilotGroupTagText));
        NextWizardStepCommand.NotifyCanExecuteChanged();
        StartDeploymentCommand.NotifyCanExecuteChanged();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName) &&
            e.PropertyName is not nameof(DeploymentSessionViewModel.IsStartupInitializing) &&
            e.PropertyName is not nameof(DeploymentSessionViewModel.CurrentPage))
        {
            return;
        }

        BeginWizardCommand.NotifyCanExecuteChanged();
    }

    private bool CanShowDebugPages()
    {
        return IsDebugSafeMode && !IsDeploymentRunning;
    }

    private bool CanUseDebugTools()
    {
        return IsDebugSafeMode && !IsDeploymentRunning;
    }

    private bool IsDebugAutopilotMode(DebugAutopilotMode mode)
    {
        return IsDebugSafeMode && _debugAutopilotMode == mode;
    }

    private void RaiseDebugAutopilotModePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsDebugAutopilotNoneMode));
        OnPropertyChanged(nameof(IsDebugAutopilotJsonProfileMode));
        OnPropertyChanged(nameof(IsDebugAutopilotHardwareHashUploadValidCertificateMode));
        OnPropertyChanged(nameof(IsDebugAutopilotHardwareHashUploadExpiredCertificateMode));
        OnPropertyChanged(nameof(IsDebugAutopilotHardwareHashUploadMissingCertificateMetadataMode));
        OnPropertyChanged(nameof(IsDebugAutopilotHardwareHashUploadNoDefaultGroupTagMode));
    }

    private bool CanBeginWizard()
    {
        return Session.IsSplashPage && Session.IsStartupReady;
    }

    private bool CanGoPrevious()
    {
        return _deploymentWizardStateService.CanGoPrevious(BuildWizardStateSnapshot());
    }

    private bool CanGoNext()
    {
        return _deploymentWizardStateService.CanGoNext(BuildWizardStateSnapshot());
    }

    private bool CanStartDeployment()
    {
        return _deploymentWizardStateService.CanStartDeployment(BuildWizardStateSnapshot());
    }

    private bool HasValidDriverPackSelection()
    {
        return DriverPackSelection.HasValidSelection();
    }

    private DeploymentWizardStateSnapshot BuildWizardStateSnapshot()
    {
        IReadOnlyList<DeploymentWizardStepDefinition> availableSteps = WizardSteps
            .Select(step => step.Definition)
            .ToArray();

        return new DeploymentWizardStateSnapshot
        {
            CurrentStepId = CurrentWizardStepId,
            AvailableSteps = availableSteps,
            IsDeploymentRunning = IsDeploymentRunning,
            IsCatalogLoading = IsCatalogLoading,
            IsTargetDiskLoading = Preparation.IsTargetDiskLoading,
            IsDebugSafeMode = IsDebugSafeMode,
            IsUnattendSelectionValid = Preparation.IsUnattendSelectionValid,
            IsTargetComputerNameValid = Preparation.IsTargetComputerNameValid,
            HasSelectedOperatingSystem = OperatingSystemCatalog.SelectedOperatingSystem is not null,
            HasTargetDiskSelection = Preparation.SelectedTargetDisk is not null,
            IsSelectedTargetDiskSelectable = Preparation.SelectedTargetDisk?.IsSelectable ?? false,
            HasValidDriverPackSelection = HasValidDriverPackSelection(),
            HasValidAutopilotSelection =
                !Preparation.IsAutopilotEnabled ||
                Preparation.AutopilotProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload ||
                Preparation.AutopilotProvisioningMode == AutopilotProvisioningMode.InteractiveHardwareHashUpload ||
                Preparation.SelectedAutopilotProfile is not null,
            IsOperatingSystemCatalogReadyForNavigation = !IsCatalogLoading && OperatingSystemCatalog.IsReadyForNavigation()
        };
    }

    partial void OnWizardStepIndexChanged(int value)
    {
        UpdateWizardStepStates();
        OnPropertyChanged(nameof(CurrentWizardStepId));
        OnPropertyChanged(nameof(IsOperatingSystemStep));
        OnPropertyChanged(nameof(IsDriversStep));
        OnPropertyChanged(nameof(IsAutopilotStep));
        OnPropertyChanged(nameof(IsSummaryStep));
        OnPropertyChanged(nameof(IsReturningToSummary));
    }

    private void RefreshWizardSteps()
    {
        IReadOnlyList<DeploymentWizardStepDefinition> definitions =
            DeploymentWizardStepDefinition.CreateSequence(HasAutopilotConfigurationStep());
        bool sequenceChanged = WizardSteps.Count != definitions.Count ||
                               WizardSteps.Select(step => step.Id).Where((id, index) => id != definitions[index].Id).Any();
        if (sequenceChanged)
        {
            _wizardNavigationState.ReplaceSteps(definitions);
        }

        WizardSteps.Clear();
        for (int index = 0; index < definitions.Count; index++)
        {
            DeploymentWizardStepDefinition definition = definitions[index];
            WizardSteps.Add(new DeploymentWizardStepViewModel(
                definition,
                GetString(definition.ResourceKey),
                index + 1,
                index == definitions.Count - 1));
        }

        SynchronizeWizardNavigation();
    }

    private void SynchronizeWizardNavigation()
    {
        WizardStepIndex = WizardSteps
            .Select((step, index) => (step, index))
            .First(item => item.step.Id == _wizardNavigationState.CurrentStepId)
            .index;
        UpdateWizardStepStates();
        OnPropertyChanged(nameof(IsReturningToSummary));
        PreviousWizardStepCommand.NotifyCanExecuteChanged();
        NextWizardStepCommand.NotifyCanExecuteChanged();
        StartDeploymentCommand.NotifyCanExecuteChanged();
    }

    private void UpdateWizardStepStates()
    {
        for (int index = 0; index < WizardSteps.Count; index++)
        {
            DeploymentWizardStepViewModel step = WizardSteps[index];
            step.IsCurrent = index == WizardStepIndex;
            step.IsCompleted = index < WizardStepIndex || _wizardNavigationState.CanNavigateTo(step.Id) && !step.IsCurrent;
            step.IsEnabled = _wizardNavigationState.CanNavigateTo(step.Id) && !step.IsCurrent;
            step.IsConnectorCompleted = _wizardNavigationState.HasAdvancedPast(step.Id);
        }
    }

    private void RefreshSummaryCategories()
    {
        bool hasCustomization = Preparation.UsesCustomUnattend || _wizardContext.Oobe.IsEnabled ||
                                _wizardContext.AppxRemoval.IsEnabled ||
                                _wizardContext.AiComponentRemoval.IsEnabled ||
                                _wizardContext.WindowsOptionalFeatures.IsEnabled;
        bool hasNetwork = _wizardContext.Network.ProfileRoaming.IsAnyEnabled;
        OperatingSystemCatalogItem? operatingSystem = SelectedOperatingSystem;
        var source = new DeploymentSummarySource
        {
            TargetSummary = Preparation.EffectiveComputerName,
            IsTargetConfigured = Preparation.IsUnattendSelectionValid && Preparation.IsTargetComputerNameValid &&
                                 (IsDebugSafeMode || Preparation.SelectedTargetDisk?.IsSelectable == true),
            HasTargetWarning = Preparation.HasUnattendWarning || Preparation.HasUnattendValidationError || Preparation.SelectedTargetDisk is { IsSelectable: false },
            TargetRows = BuildTargetSummaryRows(),
            OperatingSystemSummary = SummaryOperatingSystemText,
            IsOperatingSystemConfigured = operatingSystem is not null,
            OperatingSystemRows = operatingSystem is null
                ? [new(GetString("Summary.SelectedOperatingSystem"), GetString("Summary.NoSelection"))]
                :
                [
                    new(GetString("Summary.Release"), $"Windows {operatingSystem.WindowsRelease} {operatingSystem.ReleaseId}"),
                    new(GetString("Summary.Edition"), operatingSystem.Edition),
                    new(GetString("Summary.Architecture"), operatingSystem.Architecture),
                    new(GetString("Summary.Language"), operatingSystem.LanguageCode),
                    new(GetString("Summary.LicenseChannel"), operatingSystem.LicenseChannel),
                    new(GetString("Summary.Build"), operatingSystem.Build)
                ],
            DriversSummary = DriverPackSelection.SelectedDriverPackSelectionDisplay,
            DriverRows = BuildDriverSummaryRows(),
            IsDriversConfigured = DriverPackSelection.EffectiveSelectionKind != DriverPackSelectionKind.None,
            AutopilotSummary = Preparation.AutopilotModeText,
            AutopilotRows = BuildAutopilotSummaryRows(),
            IsAutopilotConfigured = Preparation.IsAutopilotEnabled,
            HasAutopilotStep = HasAutopilotConfigurationStep(),
            WindowsCustomizationSummary = hasCustomization
                ? GetString("Summary.Status.Configured")
                : GetString("Summary.Status.NoChanges"),
            WindowsCustomizationRows = BuildWindowsCustomizationSummaryRows(),
            IsWindowsCustomizationConfigured = hasCustomization,
            NetworkSummary = hasNetwork
                ? GetString("Summary.Status.Configured")
                : GetString("Summary.Status.NotConfigured"),
            NetworkRows = BuildNetworkSummaryRows(),
            IsNetworkConfigured = hasNetwork,
            CompletionSummary = _wizardContext.Completion.AutomaticRebootEnabled
                ? GetString("Summary.AutomaticRestart")
                : GetString("Summary.ManualRestart"),
            CompletionRows = BuildCompletionSummaryRows()
        };

        var builder = new DeploymentSummaryBuilder(GetString);
        SummaryCategories.Clear();
        foreach (DeploymentSummaryCategoryViewModel category in builder.Build(source))
        {
            SummaryCategories.Add(category);
        }
    }

    private IReadOnlyList<DeploymentSummaryRowViewModel> BuildTargetSummaryRows()
    {
        List<DeploymentSummaryRowViewModel> rows =
        [
            DeploymentSummaryRowViewModel.Section(GetString("TargetDevice.DeploymentSettings")),
            new(GetString("Unattend.Title"), Preparation.UnattendSummary),
            new(GetString("Preparation.ComputerName"), Preparation.EffectiveComputerName),
            new(GetString("Unattend.TimeZone"), Preparation.UsesCustomUnattend ? GetString("Unattend.Managed") : _wizardContext.DefaultTimeZoneId ?? GetString("Common.None")),
            new(GetString("Summary.TargetDisk"), SummaryTargetDiskText),
            new(GetString("TargetDevice.Firmware"), SummaryFirmwareText),
            DeploymentSummaryRowViewModel.Separator(),
            DeploymentSummaryRowViewModel.Section(GetString("TargetDevice.DeviceInventory")),
            new(GetString("TargetDevice.Manufacturer"), Preparation.HardwareManufacturerText),
            new(GetString("TargetDevice.Model"), Preparation.HardwareModelText),
            new(GetString("TargetDevice.Product"), Preparation.HardwareProductText),
            new(GetString("TargetDevice.Architecture"), Preparation.HardwareArchitectureText),
            new(GetString("TargetDevice.Tpm"), Preparation.HardwareTpmText),
            new(GetString("TargetDevice.PowerSource"), Preparation.HardwarePowerText),
            new(GetString("TargetDevice.FirmwareStatus"), Preparation.HardwareFirmwareText)
        ];
        if (Preparation.HasUnattendWarning)
            rows.Add(new(GetString("Summary.WarningDetails"), Preparation.UnattendWarning));
        if (Preparation.HasUnattendValidationError)
            rows.Add(new(GetString("Summary.Status"), Preparation.UnattendValidationMessage));
        return rows;
    }

    private IReadOnlyList<DeploymentSummaryRowViewModel> BuildDriverSummaryRows()
    {
        if (DriverPackSelection.EffectiveSelectionKind == DriverPackSelectionKind.None)
        {
            return [new(GetString("DriverPack.Source"), GetString("Common.None"))];
        }

        if (DriverPackSelection.EffectiveSelectionKind == DriverPackSelectionKind.MicrosoftUpdateCatalog)
        {
            return [new(GetString("DriverPack.Source"), GetString("DriverPack.MicrosoftUpdateCatalog"))];
        }

        string manufacturer = DriverPackSelection.SelectedDriverPackOption?.DisplayName ?? GetString("DriverPack.Oem");
        DriverPackCatalogItem? selectedPack = DriverPackSelection.ResolveEffectiveDriverPackSelection();
        if (selectedPack is null)
        {
            return
            [
                new(GetString("TargetDevice.Manufacturer"), manufacturer),
                new(GetString("Summary.Status"), Format("DriverPack.NoMatchingModelVersionFormat", manufacturer))
            ];
        }

        return
        [
            new(GetString("TargetDevice.Manufacturer"), selectedPack.Manufacturer),
            new(GetString("DriverPack.Model"), DriverPackSelection.SelectedDriverPackModel),
            new(GetString("DriverPack.Version"), DriverPackSelection.SelectedDriverPackVersion)
        ];
    }

    private bool HasAutopilotConfigurationStep()
    {
        return Preparation.IsAutopilotEnabled &&
               Preparation.AutopilotProvisioningMode != AutopilotProvisioningMode.InteractiveHardwareHashUpload;
    }

    private IReadOnlyList<DeploymentSummaryRowViewModel> BuildAutopilotSummaryRows()
    {
        if (!Preparation.IsAutopilotEnabled)
        {
            return [];
        }

        var rows = new List<DeploymentSummaryRowViewModel>
        {
            new(GetString("Summary.ProvisioningMethod"), Preparation.AutopilotModeText)
        };
        if (Preparation.IsJsonProfileMode)
        {
            rows.Add(new(GetString("Summary.AutopilotProfile"), SummaryAutopilotProfileText));
        }
        else if (Preparation.IsHardwareHashUploadMode)
        {
            rows.Add(new(
                GetString("Preparation.AutopilotHardwareHashUploadStatus"),
                Preparation.AutopilotHardwareHashUploadStatusText));
            rows.Add(new(GetString("Summary.AutopilotGroupTag"), SummaryAutopilotGroupTagText));
            rows.Add(DeploymentSummaryRowViewModel.Separator());
            rows.Add(DeploymentSummaryRowViewModel.Section(GetString("Autopilot.ConfigurationDetails")));
            rows.Add(new(
                GetString("Preparation.AutopilotHardwareHashTenantId"),
                Preparation.AutopilotHardwareHashTenantIdText));
            rows.Add(new(
                GetString("Preparation.AutopilotHardwareHashCertificateExpiration"),
                Preparation.AutopilotHardwareHashCertificateExpirationText));
            if (Preparation.IsHardwareHashUploadMessageVisible)
            {
                rows.Add(new(GetString("Summary.WarningDetails"), Preparation.AutopilotHardwareHashUploadMessage));
            }
        }

        return rows;
    }

    private IReadOnlyList<DeploymentSummaryRowViewModel> BuildWindowsCustomizationSummaryRows()
    {
        return WindowsCustomizationSummaryBuilder.Build(
            _wizardContext.Oobe,
            _wizardContext.AppxRemoval,
            _wizardContext.AiComponentRemoval,
            _wizardContext.WindowsOptionalFeatures,
            GetString,
            CurrentCulture,
            Preparation.UsesCustomUnattend);
    }

    private IReadOnlyList<DeploymentSummaryRowViewModel> BuildNetworkSummaryRows()
    {
        var roaming = _wizardContext.Network.ProfileRoaming;
        if (!roaming.IsAnyEnabled)
        {
            return [new(GetString("Summary.NetworkProfileRoaming"), GetString("Common.Disabled"))];
        }

        var rows = new List<DeploymentSummaryRowViewModel>();
        AddNetworkTransportRows(rows, "Summary.WiredDot1x", roaming.WiredDot1x.IsEnabled, roaming.WiredDot1x.IncludePrivateKeyMaterial);
        rows.Add(DeploymentSummaryRowViewModel.Separator());
        AddNetworkTransportRows(rows, "Summary.Wifi", roaming.Wifi.IsEnabled, roaming.Wifi.IncludePrivateKeyMaterial);
        return rows;
    }

    private void AddNetworkTransportRows(
        ICollection<DeploymentSummaryRowViewModel> rows,
        string sectionKey,
        bool isEnabled,
        bool includePrivateKeyMaterial)
    {
        rows.Add(DeploymentSummaryRowViewModel.Section(GetString(sectionKey)));
        rows.Add(new(GetString("Summary.NetworkProfileRoaming"), GetEnabledText(isEnabled)));
        if (isEnabled)
        {
            rows.Add(new(
                GetString("Summary.PrivateKeyMaterial"),
                GetString(includePrivateKeyMaterial ? "Summary.Included" : "Summary.NotIncluded")));
        }
    }

    private IReadOnlyList<DeploymentSummaryRowViewModel> BuildCompletionSummaryRows()
    {
        var rows = new List<DeploymentSummaryRowViewModel>
        {
            new(
                GetString("Summary.RestartBehavior"),
                GetString(_wizardContext.Completion.AutomaticRebootEnabled ? "Summary.Automatic" : "Summary.Manual"))
        };
        if (_wizardContext.Completion.AutomaticRebootEnabled)
        {
            rows.Add(new(
                GetString("Summary.RestartDelay"),
                Format("Summary.SecondsFormat", _wizardContext.Completion.AutomaticRebootDelaySeconds)));
        }

        return rows;
    }

    private string GetEnabledText(bool enabled)
    {
        return enabled ? GetString("Common.Enabled") : GetString("Common.Disabled");
    }

    private static string ResolveInitialComputerName()
    {
        string normalized = ComputerNameRules.Normalize(Environment.MachineName);
        return normalized.Length > 0
            ? normalized
            : ComputerNameRules.FallbackName;
    }

    private void ApplyStartupSnapshot(DeploymentStartupSnapshot startupSnapshot)
    {
        ArgumentNullException.ThrowIfNull(startupSnapshot);

        _wizardContext.ApplyStartupSnapshot(startupSnapshot);
        IsBootMediaUpdateRecommended = startupSnapshot.IsBootMediaUpdateRecommended;
        Session.ConfigureRebootPolicy(DeploymentRebootPolicy.Create(_wizardContext.Completion));
        Session.SetComputerName(Preparation.EffectiveComputerName);
        Session.CompleteStartupInitialization();
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    public override void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _wizardContext.StateChanged -= OnWizardContextStateChanged;
        Session.PropertyChanged -= OnSessionPropertyChanged;
        LocalizationService.LanguageChanged -= OnLocalizationLanguageChanged;
        _wizardContext.Dispose();
        Session.Dispose();
        _isDisposed = true;
        base.Dispose();
    }

    private void OnLocalizationLanguageChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            RefreshSupportedCultures();
            OnPropertyChanged(nameof(CurrentCulture));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(VersionDisplay));
            OnPropertyChanged(nameof(BootMediaUpdateRecommendedText));
            OnPropertyChanged(nameof(BootMediaUpdateRecommendedToolTip));
            OperatingSystemCatalog.RefreshLocalizedMediaOptions();
            OnPropertyChanged(nameof(OperatingSystemArchitectureDisplay));
            OnPropertyChanged(nameof(SummaryTargetDiskText));
            OnPropertyChanged(nameof(SummaryOperatingSystemText));
            OnPropertyChanged(nameof(SummaryFirmwareText));
            OnPropertyChanged(nameof(SummaryAutopilotProfileText));
            OnPropertyChanged(nameof(SummaryAutopilotGroupTagText));
            RefreshWizardSteps();
            RefreshSummaryCategories();
        });
    }

    private string GetString(string key)
    {
        return Strings[key];
    }

    private string Format(string key, params object[] args)
    {
        return string.Format(LocalizationService.CurrentCulture, GetString(key), args);
    }
}
