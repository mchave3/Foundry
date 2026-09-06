// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.Autopilot;
using Foundry.Core.Services.Application;
using Foundry.Core.Services.Configuration;
using Foundry.Core.Services.Media;
using Foundry.Core.Services.Telemetry;
using Foundry.Core.Services.WinPe;
using Foundry.Services.Adk;
using Foundry.Services.Configuration;
using Foundry.Services.Localization;
using Foundry.Services.Operations;
using Foundry.Services.Settings;
using Foundry.Services.Shell;
using Foundry.Telemetry;
using Serilog;
using Serilog.Context;

namespace Foundry.ViewModels;

/// <summary>
/// Coordinates media creation options, readiness evaluation, and final ISO or USB creation commands.
/// </summary>
public sealed partial class StartMediaViewModel : ObservableObject, IDisposable
{
    private readonly IAppSettingsService appSettingsService;
    private readonly IAdkService adkService;
    private readonly IWinPeLanguageDiscoveryService languageDiscoveryService;
    private readonly IWinPeEmbeddedAssetService embeddedAssetService;
    private readonly IWinPeBuildService buildService;
    private readonly IWinPeWorkspacePreparationService workspacePreparationService;
    private readonly IWinPeIsoMediaService isoMediaService;
    private readonly IWinPeUsbMediaService usbMediaService;
    private readonly IFilePickerService filePickerService;
    private readonly IFoundryConfigurationStateService foundryConfigurationStateService;
    private readonly IConfigurationOverviewService configurationOverviewService;
    private readonly IDeploymentProtectionSecretStateService deploymentProtectionSecretStateService;
    private readonly INetworkSecretStateService networkSecretStateService;
    private readonly ITelemetryService telemetryService;
    private readonly IOperationProgressService operationProgressService;
    private readonly IShellNavigationGuardService shellNavigationGuardService;
    private readonly IDialogService dialogService;
    private readonly IApplicationLocalizationService localizationService;
    private readonly IAppDispatcher appDispatcher;
    private readonly ILogger logger;
    private IReadOnlyList<string> availableWinPeLanguages = [];
    private UsbCandidateDiscoveryState usbCandidateDiscoveryState = UsbCandidateDiscoveryState.NotLoaded;
    private bool isLoadingConfiguration = true;
    private string? lastCustomizationLogStatus;
    private int lastCustomizationLogBucket = -1;
    private string? lastDownloadLogStatus;
    private int lastDownloadLogBucket = -1;
    private string? lastFinalMediaLogStatus;
    private int lastFinalMediaLogBucket = -1;
    private bool isRefreshingUnattendSources;
    private bool isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartMediaViewModel"/> class.
    /// </summary>
    public StartMediaViewModel(
        IAppSettingsService appSettingsService,
        IAdkService adkService,
        IWinPeLanguageDiscoveryService languageDiscoveryService,
        IWinPeEmbeddedAssetService embeddedAssetService,
        IWinPeBuildService buildService,
        IWinPeWorkspacePreparationService workspacePreparationService,
        IWinPeIsoMediaService isoMediaService,
        IWinPeUsbMediaService usbMediaService,
        IFilePickerService filePickerService,
        IFoundryConfigurationStateService foundryConfigurationStateService,
        IConfigurationOverviewService configurationOverviewService,
        IDeploymentProtectionSecretStateService deploymentProtectionSecretStateService,
        INetworkSecretStateService networkSecretStateService,
        ITelemetryService telemetryService,
        IOperationProgressService operationProgressService,
        IShellNavigationGuardService shellNavigationGuardService,
        IDialogService dialogService,
        IApplicationLocalizationService localizationService,
        IAppDispatcher appDispatcher,
        ILogger logger)
    {
        this.appSettingsService = appSettingsService;
        this.adkService = adkService;
        this.languageDiscoveryService = languageDiscoveryService;
        this.embeddedAssetService = embeddedAssetService;
        this.buildService = buildService;
        this.workspacePreparationService = workspacePreparationService;
        this.isoMediaService = isoMediaService;
        this.usbMediaService = usbMediaService;
        this.filePickerService = filePickerService;
        this.foundryConfigurationStateService = foundryConfigurationStateService;
        this.configurationOverviewService = configurationOverviewService;
        this.deploymentProtectionSecretStateService = deploymentProtectionSecretStateService;
        this.networkSecretStateService = networkSecretStateService;
        this.telemetryService = telemetryService;
        this.operationProgressService = operationProgressService;
        this.shellNavigationGuardService = shellNavigationGuardService;
        this.dialogService = dialogService;
        this.localizationService = localizationService;
        this.appDispatcher = appDispatcher;
        this.logger = logger.ForContext<StartMediaViewModel>();

        Architectures =
        [
            new(WinPeArchitecture.X64, "x64"),
            new(WinPeArchitecture.Arm64, "arm64")
        ];
        PartitionStyles = [];
        FormatModes = [];

        GeneralSettings general = foundryConfigurationStateService.Current.General;
        IsoOutputPath = general.IsoOutputPath ?? string.Empty;
        SelectedArchitecture = SelectOption(Architectures, general.Architecture);
        UseCa2023Signature = general.UseCa2023;
        RebuildPartitionStyles();
        IncludeDellDrivers = general.IncludeDellDrivers;
        IncludeHpDrivers = general.IncludeHpDrivers;
        CustomDriverDirectoryPath = general.CustomDriverDirectoryPath ?? string.Empty;

        configurationOverviewService.Changed += OnConfigurationOverviewChanged;
        localizationService.LanguageChanged += OnLanguageChanged;

        isLoadingConfiguration = false;
        ApplyLocalizedText();
        RefreshEvaluation();
    }

    /// <summary>
    /// Gets the available WinPE architecture options.
    /// </summary>
    public ObservableCollection<SelectionOption<WinPeArchitecture>> Architectures { get; }

    /// <summary>
    /// Gets the USB partition style options valid for the selected architecture.
    /// </summary>
    public ObservableCollection<SelectionOption<UsbPartitionStyle>> PartitionStyles { get; }

    /// <summary>
    /// Gets the USB formatting mode options.
    /// </summary>
    public ObservableCollection<SelectionOption<UsbFormatMode>> FormatModes { get; }

    /// <summary>
    /// Gets removable USB disk candidates discovered for media creation.
    /// </summary>
    public ObservableCollection<SelectionOption<WinPeUsbDiskCandidate>> UsbCandidates { get; } = [];

    /// <summary>
    /// Gets the General configuration overview rows.
    /// </summary>
    public ObservableCollection<StartConfigurationOverviewItemViewModel> GeneralConfigurationOverviewItems { get; } = [];

    /// <summary>
    /// Gets the network configuration overview rows.
    /// </summary>
    public ObservableCollection<StartConfigurationOverviewItemViewModel> NetworkOverviewItems { get; } = [];

    /// <summary>
    /// Gets the Windows Autopilot overview rows.
    /// </summary>
    public ObservableCollection<StartConfigurationOverviewItemViewModel> AutopilotOverviewItems { get; } = [];

    /// <summary>
    /// Gets the customization overview rows.
    /// </summary>
    public ObservableCollection<StartConfigurationOverviewItemViewModel> CustomizationOverviewItems { get; } = [];

    [ObservableProperty]
    public partial string PageTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PageDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IsoOutputPath { get; set; }

    [ObservableProperty]
    public partial SelectionOption<WinPeArchitecture>? SelectedArchitecture { get; set; }

    [ObservableProperty]
    public partial bool UseCa2023Signature { get; set; }

    [ObservableProperty]
    public partial SelectionOption<UsbPartitionStyle>? SelectedPartitionStyle { get; set; }

    [ObservableProperty]
    public partial SelectionOption<UsbFormatMode>? SelectedFormatMode { get; set; }

    [ObservableProperty]
    public partial bool IncludeDellDrivers { get; set; }

    [ObservableProperty]
    public partial bool IncludeHpDrivers { get; set; }

    [ObservableProperty]
    public partial string CustomDriverDirectoryPath { get; set; }

    [ObservableProperty]
    public partial SelectionOption<WinPeUsbDiskCandidate>? SelectedUsbDisk { get; set; }

    [ObservableProperty]
    public partial bool IsSelectedUsbFoundryMedia { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshingUsbCandidates { get; set; }

    [ObservableProperty]
    public partial bool CanGenerateIsoSummary { get; set; }

    [ObservableProperty]
    public partial bool CanGenerateUsbSummary { get; set; }

    [ObservableProperty]
    public partial bool CanSelectUsbDisk { get; set; }

    [ObservableProperty]
    public partial bool CanCreateIso { get; set; }

    [ObservableProperty]
    public partial bool CanCreateUsb { get; set; }

    [ObservableProperty]
    public partial bool IsMediaOperationRunning { get; set; }

    [ObservableProperty]
    public partial string StatusSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FinalExecutionStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GlobalSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WinPeLanguage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UsbCandidateStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity ReadinessSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial bool IsGeneralConfigurationOverviewExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsNetworkOverviewExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsAutopilotOverviewExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsCustomizationOverviewExpanded { get; set; }

    [ObservableProperty]
    public partial string GeneralConfigurationOverviewSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NetworkOverviewSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AutopilotOverviewSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CustomizationOverviewSummary { get; set; } = string.Empty;

    public string DocumentationUrl => FoundryApplicationInfo.StartDocumentationUrl;

    /// <inheritdoc />
    public void Dispose()
    {
        isDisposed = true;
        configurationOverviewService.Changed -= OnConfigurationOverviewChanged;
        localizationService.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    /// Refreshes readiness and disk candidates after the page is loaded.
    /// </summary>
    public async Task InitializeAsync()
    {
        RefreshEvaluation();

        if (adkService.CurrentStatus.CanCreateMedia)
        {
            await RefreshUsbCandidatesAsync();
        }
        else
        {
            await RefreshUnattendSourcesAsync();
        }

        MediaPreflightOptions options = CreatePreflightOptions();
        LogPreflightSummary(options, MediaPreflightService.Evaluate(options));
    }

    [RelayCommand]
    private async Task BrowseIsoOutputPathAsync()
    {
        string? path = await filePickerService.PickSaveFileAsync(
            new FileSavePickerRequest(
                localizationService.GetString("StartMedia.IsoPicker.Title"),
                "Foundry",
                (FilePickerTypeChoice[])[new(
                    localizationService.GetString("StartMedia.IsoPicker.Filter"),
                    (string[])[".iso"])],
                ".iso"));

        if (!string.IsNullOrWhiteSpace(path))
        {
            IsoOutputPath = path;
        }
    }

    [RelayCommand]
    private async Task RefreshUsbCandidatesAsync()
    {
        await RefreshUnattendSourcesAsync();
        if (isDisposed)
        {
            return;
        }

        if (!adkService.CurrentStatus.CanCreateMedia)
        {
            usbCandidateDiscoveryState = UsbCandidateDiscoveryState.Blocked;
            UsbCandidateStatus = localizationService.GetString("StartMedia.Usb.AdkBlocked");
            RefreshEvaluation();
            return;
        }

        WinPeResult<WinPeToolPaths> toolsResult = new WinPeToolResolver().ResolveTools(adkService.CurrentStatus.KitsRootPath);
        if (!toolsResult.IsSuccess || toolsResult.Value is null)
        {
            usbCandidateDiscoveryState = UsbCandidateDiscoveryState.Error;
            UsbCandidateStatus = toolsResult.Error?.Message ?? localizationService.GetString("StartMedia.Usb.QueryFailed");
            logger.Warning("USB target refresh skipped because ADK tools were not resolved. ErrorCode={ErrorCode}", toolsResult.Error?.Code);
            RefreshEvaluation();
            return;
        }

        IsRefreshingUsbCandidates = true;
        usbCandidateDiscoveryState = UsbCandidateDiscoveryState.Loading;
        UsbCandidateStatus = localizationService.GetString("StartMedia.Usb.Loading");
        RefreshEvaluation();

        try
        {
            WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>> result = await usbMediaService.GetUsbCandidatesAsync(
                toolsResult.Value,
                Constants.UsbQueryTempDirectoryPath,
                CancellationToken.None);

            UsbCandidates.Clear();
            if (result.IsSuccess && result.Value is not null)
            {
                foreach (WinPeUsbDiskCandidate candidate in result.Value)
                {
                    UsbCandidates.Add(CreateUsbDiskOption(candidate));
                }

                SelectedUsbDisk = UsbCandidates.FirstOrDefault(option => option.Value.DiskNumber == SelectedUsbDisk?.Value.DiskNumber)
                    ?? UsbCandidates.FirstOrDefault();
                usbCandidateDiscoveryState = UsbCandidates.Count == 0
                    ? UsbCandidateDiscoveryState.Empty
                    : UsbCandidateDiscoveryState.Ready;
                UsbCandidateStatus = UsbCandidates.Count == 0
                    ? localizationService.GetString("StartMedia.Usb.NoCandidatesFound")
                    : string.Format(localizationService.GetString("StartMedia.Usb.CandidatesFound"), UsbCandidates.Count);
                logger.Information("USB targets refreshed. CandidateCount={CandidateCount}", UsbCandidates.Count);
            }
            else
            {
                SelectedUsbDisk = null;
                usbCandidateDiscoveryState = UsbCandidateDiscoveryState.Error;
                UsbCandidateStatus = result.Error?.Message ?? localizationService.GetString("StartMedia.Usb.QueryFailed");
                logger.Warning("USB target refresh failed. ErrorCode={ErrorCode}", result.Error?.Code);
            }
        }
        finally
        {
            IsRefreshingUsbCandidates = false;
            RefreshEvaluation();
        }
    }

    [RelayCommand]
    private async Task CreateIsoAsync()
    {
        if (IsMediaOperationRunning || isRefreshingUnattendSources)
        {
            return;
        }

        await RefreshUnattendSourcesAsync();
        if (isDisposed)
        {
            return;
        }

        SynchronizeRuntimeTelemetrySettings();

        MediaPreflightOptions options = CreatePreflightOptions();
        MediaPreflightEvaluation evaluation = MediaPreflightService.Evaluate(options);
        if (!evaluation.CanCreateIso)
        {
            logger.Information(
                "ISO media creation was blocked by preflight validation. BlockingReasons={BlockingReasons}",
                string.Join(",", evaluation.IsoBlockingReasons));
            await ShowBlockedDialogAsync("StartMedia.CreateIso.BlockedTitle", evaluation.IsoBlockingReasons);
            return;
        }

        await RunFinalMediaOperationAsync(FinalMediaTarget.Iso, options);
    }

    [RelayCommand]
    private async Task CreateUsbAsync()
    {
        if (IsMediaOperationRunning || isRefreshingUnattendSources)
        {
            return;
        }

        await RefreshUnattendSourcesAsync();
        if (isDisposed)
        {
            return;
        }

        SynchronizeRuntimeTelemetrySettings();

        MediaPreflightOptions options = CreatePreflightOptions();
        MediaPreflightEvaluation evaluation = MediaPreflightService.Evaluate(options);
        if (!evaluation.CanCreateUsb || options.SelectedUsbDisk is null)
        {
            logger.Information(
                "USB media creation was blocked by preflight validation. HasSelectedUsbTarget={HasSelectedUsbTarget}, BlockingReasons={BlockingReasons}",
                options.SelectedUsbDisk is not null,
                string.Join(",", evaluation.UsbBlockingReasons));
            await ShowBlockedDialogAsync("StartMedia.CreateUsb.BlockedTitle", evaluation.UsbBlockingReasons);
            return;
        }

        if (IsSelectedUsbFoundryMedia)
        {
            await RunFinalMediaOperationAsync(FinalMediaTarget.UsbUpdate, options);
            return;
        }

        WinPeUsbDiskCandidate selectedDisk = options.SelectedUsbDisk;
        bool confirmed = await dialogService.ConfirmAsync(new ConfirmationDialogRequest(
            localizationService.GetString("StartMedia.CreateUsb.ConfirmTitle"),
            string.Format(
                localizationService.GetString("StartMedia.CreateUsb.ConfirmMessage"),
                selectedDisk.DiskNumber,
                selectedDisk.FriendlyName,
                FormatByteSize(selectedDisk.SizeBytes)),
            localizationService.GetString("StartMedia.CreateUsb.ConfirmPrimary"),
            localizationService.GetString("Common.Cancel")));

        if (!confirmed)
        {
            logger.Information(
                "Final USB media creation cancelled before formatting. DiskNumber={DiskNumber}, DiskName={DiskName}",
                selectedDisk.DiskNumber,
                selectedDisk.FriendlyName);
            return;
        }

        await RunFinalMediaOperationAsync(FinalMediaTarget.Usb, options);
    }

    private async Task RunFinalMediaOperationAsync(FinalMediaTarget target, MediaPreflightOptions options)
    {
        if (IsMediaOperationRunning)
        {
            return;
        }

        IsMediaOperationRunning = true;
        ResetProgressLogSampling();
        RefreshEvaluation();
        // Final media operations can format disks or overwrite ISO output, so the shell remains locked until completion.
        shellNavigationGuardService.SetState(ShellNavigationState.OperationRunning);
        string terminalStatus = string.Empty;
        string? successMessage = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool success = false;
        var telemetryProgressTracker = new MediaCreationTelemetryProgressTracker();
        string operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string bootMediaTarget = target == FinalMediaTarget.Iso
            ? TelemetryBootMediaTargets.Iso
            : TelemetryBootMediaTargets.Usb;
        string usbOperation = target switch
        {
            FinalMediaTarget.Usb => TelemetryBootMediaUsbOperations.Create,
            FinalMediaTarget.UsbUpdate => TelemetryBootMediaUsbOperations.Update,
            _ => TelemetryBootMediaUsbOperations.None
        };
        WinPeDiagnostic? failureDiagnostic = null;
        DeploymentMediaProtectionMaterial? deploymentProtectionMaterial = null;

        using IDisposable operationIdScope = LogContext.PushProperty("OperationId", operationId);
        using IDisposable workflowScope = LogContext.PushProperty("Workflow", "boot_media_creation");
        using IDisposable targetScope = LogContext.PushProperty("BootMediaTarget", bootMediaTarget);
        using IDisposable usbOperationScope = LogContext.PushProperty("UsbOperation", usbOperation);
        using IDisposable stageScope = LogContext.Push(telemetryProgressTracker);

        try
        {
            deploymentProtectionMaterial = CreateDeploymentProtectionMaterial();
            string startStatus = target switch
            {
                FinalMediaTarget.Iso => localizationService.GetString("StartMedia.Operation.CreatingIso"),
                FinalMediaTarget.UsbUpdate => localizationService.GetString("StartMedia.Operation.UpdatingUsb"),
                _ => localizationService.GetString("StartMedia.Operation.CreatingUsb")
            };

            operationProgressService.Start(OperationKind.MediaCreation, startStatus);
            logger.Information(
                "Final media creation started. Target={Target}, Architecture={Architecture}, WinPeLanguage={WinPeLanguage}, IsoOutputPath={IsoOutputPath}, UsbDiskNumber={UsbDiskNumber}, UsbDiskName={UsbDiskName}",
                target,
                options.Architecture,
                NormalizeCultureName(options.WinPeLanguage),
                target == FinalMediaTarget.Iso ? options.IsoOutputPath : null,
                target is FinalMediaTarget.Usb or FinalMediaTarget.UsbUpdate ? options.SelectedUsbDisk?.DiskNumber : null,
                target is FinalMediaTarget.Usb or FinalMediaTarget.UsbUpdate ? options.SelectedUsbDisk?.FriendlyName : null);

            if (target == FinalMediaTarget.Iso)
            {
                _ = await CreateIsoMediaAsync(options, deploymentProtectionMaterial, telemetryProgressTracker, CancellationToken.None);
                successMessage = localizationService.GetString("StartMedia.Operation.IsoSuccessMessage");
            }
            else if (target == FinalMediaTarget.UsbUpdate)
            {
                WinPeUsbProvisionResult usbResult = await UpdateUsbMediaAsync(options, deploymentProtectionMaterial, telemetryProgressTracker, CancellationToken.None);
                successMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    localizationService.GetString("StartMedia.Operation.UsbUpdateSuccessMessage"),
                    usbResult.BootDriveLetter,
                    usbResult.CacheDriveLetter);
            }
            else
            {
                WinPeUsbProvisionResult usbResult = await CreateUsbMediaAsync(options, deploymentProtectionMaterial, telemetryProgressTracker, CancellationToken.None);
                successMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    localizationService.GetString("StartMedia.Operation.UsbSuccessMessage"),
                    usbResult.BootDriveLetter,
                    usbResult.CacheDriveLetter);
            }

            terminalStatus = successMessage ?? localizationService.GetString("StartMedia.Operation.Completed");
            operationProgressService.Complete(terminalStatus);
            success = true;
            logger.Information(
                "Final media creation completed. Target={Target}, DurationMs={DurationMs}",
                target,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failureDiagnostic = (ex as WinPeOperationException)?.Diagnostic ?? new WinPeDiagnostic(
                WinPeErrorCodes.InternalError,
                "Unexpected boot media creation failure.",
                ex.ToString(),
                telemetryProgressTracker.CurrentStepName,
                failureKind: WinPeFailureKinds.Internal,
                failureReason: WinPeFailureReasons.Unexpected,
                errorSummary: ex.Message,
                exception: ex);
            string failedStepName = string.IsNullOrWhiteSpace(failureDiagnostic.Stage)
                ? telemetryProgressTracker.CurrentStepName
                : failureDiagnostic.Stage;
            telemetryProgressTracker.SetCurrentStep(failedStepName);
            string failedStatus = localizationService.GetString("StartMedia.Operation.Failed");
            terminalStatus = string.IsNullOrWhiteSpace(ex.Message)
                ? failedStatus
                : $"{failedStatus} {ex.Message}";
            operationProgressService.Report(100, terminalStatus);
            logger.Error(
                ex,
                "Final boot media operation failed. FailedStepName={FailedStepName}, DurationMs={DurationMs}, FailureKind={FailureKind}, FailureReason={FailureReason}, FailureCode={FailureCode}, ToolName={ToolName}, ExitCode={ExitCode}, RetryCount={RetryCount}, ErrorSummary={ErrorSummary}, RemoteDiagnostic={RemoteDiagnostic}",
                failedStepName,
                stopwatch.ElapsedMilliseconds,
                failureDiagnostic.FailureKind,
                failureDiagnostic.FailureReason,
                failureDiagnostic.Code,
                failureDiagnostic.ToolName,
                failureDiagnostic.ExitCode,
                failureDiagnostic.RetryCount,
                failureDiagnostic.ErrorSummary ?? failureDiagnostic.Message,
                true);
        }
        catch (OperationCanceledException ex)
        {
            failureDiagnostic = new WinPeDiagnostic(
                WinPeErrorCodes.OperationCancelled,
                "Boot media creation was cancelled.",
                ex.ToString(),
                telemetryProgressTracker.CurrentStepName,
                failureKind: WinPeFailureKinds.Cancellation,
                failureReason: WinPeFailureReasons.Cancelled,
                errorSummary: ex.Message,
                exception: ex);
            logger.Warning(
                ex,
                "Final boot media operation cancelled. FailedStepName={FailedStepName}, DurationMs={DurationMs}, FailureKind={FailureKind}, FailureReason={FailureReason}, FailureCode={FailureCode}, RetryCount={RetryCount}, RemoteDiagnostic={RemoteDiagnostic}",
                telemetryProgressTracker.CurrentStepName,
                stopwatch.ElapsedMilliseconds,
                failureDiagnostic.FailureKind,
                failureDiagnostic.FailureReason,
                failureDiagnostic.Code,
                failureDiagnostic.RetryCount,
                true);
            throw;
        }
        finally
        {
            deploymentProtectionMaterial?.Dispose();
            await TrackMediaCreatedAsync(
                target,
                options,
                success,
                success ? null : telemetryProgressTracker.CurrentStepName,
                stopwatch.Elapsed,
                operationId,
                failureDiagnostic,
                CancellationToken.None);
            shellNavigationGuardService.SetState(adkService.CurrentStatus.CanCreateMedia
                ? ShellNavigationState.Ready
                : ShellNavigationState.AdkBlocked);
            operationProgressService.Reset(terminalStatus);
            IsMediaOperationRunning = false;
            RefreshEvaluation();
        }
    }

    private async Task<string> CreateIsoMediaAsync(
        MediaPreflightOptions options,
        DeploymentMediaProtectionMaterial deploymentProtectionMaterial,
        MediaCreationTelemetryProgressTracker telemetryProgressTracker,
        CancellationToken cancellationToken)
    {
        PreparedMediaWorkspace? workspace = null;

        try
        {
            workspace = await PrepareMediaWorkspaceAsync(
                options,
                includeRuntimePayloadInImage: true,
                deploymentProtectionMaterial,
                telemetryProgressTracker: telemetryProgressTracker,
                cancellationToken);

            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.CreateIsoMedia);
            logger.Debug(
                "Creating ISO media. OutputIsoPath={OutputIsoPath}, MediaDirectoryPath={MediaDirectoryPath}, UseBootEx={UseBootEx}",
                options.IsoOutputPath,
                workspace.PreparedWorkspace.Artifact.MediaDirectoryPath,
                workspace.PreparedWorkspace.UseBootEx);

            WinPeResult result = await isoMediaService.CreateAsync(
                new WinPeIsoMediaOptions
                {
                    PreparedWorkspace = workspace.PreparedWorkspace,
                    OutputIsoPath = options.IsoOutputPath,
                    IsoTempDirectoryPath = Path.Combine(Constants.TempDirectoryPath, "Iso"),
                    Progress = telemetryProgressTracker.CreateFinalMediaProgress(
                        new Progress<WinPeMediaProgress>(ReportFinalMediaProgress))
                },
                cancellationToken);

            EnsureSuccess(result);
            logger.Debug("ISO media service completed. OutputIsoPath={OutputIsoPath}", options.IsoOutputPath);
            return options.IsoOutputPath;
        }
        finally
        {
            CleanupPreparedWorkspace(workspace?.PreparedWorkspace.Artifact.WorkingDirectoryPath);
        }
    }

    private async Task<WinPeUsbProvisionResult> CreateUsbMediaAsync(
        MediaPreflightOptions options,
        DeploymentMediaProtectionMaterial deploymentProtectionMaterial,
        MediaCreationTelemetryProgressTracker telemetryProgressTracker,
        CancellationToken cancellationToken)
    {
        telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.ValidateUsbTarget);
        if (options.SelectedUsbDisk is null)
        {
            throw new InvalidOperationException(localizationService.GetString("StartMedia.BlockingReason.NoUsbTarget"));
        }

        PreparedMediaWorkspace? workspace = null;

        try
        {
            workspace = await PrepareMediaWorkspaceAsync(
                options,
                includeRuntimePayloadInImage: false,
                deploymentProtectionMaterial,
                telemetryProgressTracker: telemetryProgressTracker,
                cancellationToken);

            WinPeUsbDiskCandidate selectedDisk = options.SelectedUsbDisk;
            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.CreateUsbMedia);
            logger.Debug(
                "Creating USB media. DiskNumber={DiskNumber}, DiskName={DiskName}, PartitionStyle={PartitionStyle}, FormatMode={FormatMode}, MediaDirectoryPath={MediaDirectoryPath}, UseBootEx={UseBootEx}",
                selectedDisk.DiskNumber,
                selectedDisk.FriendlyName,
                options.UsbPartitionStyle,
                options.UsbFormatMode,
                workspace.PreparedWorkspace.Artifact.MediaDirectoryPath,
                workspace.PreparedWorkspace.UseBootEx);

            WinPeResult<WinPeUsbProvisionResult> result = await usbMediaService.ProvisionAndPopulateAsync(
                new UsbOutputOptions
                {
                    TargetDiskNumber = selectedDisk.DiskNumber,
                    ExpectedDiskFriendlyName = selectedDisk.FriendlyName,
                    ExpectedDiskSerialNumber = selectedDisk.SerialNumber,
                    ExpectedDiskUniqueId = selectedDisk.UniqueId,
                    PartitionStyle = options.UsbPartitionStyle,
                    FormatMode = options.UsbFormatMode,
                    RuntimePayloadProvisioning = workspace.RuntimePayloadProvisioning,
                    DownloadProgress = telemetryProgressTracker.CreateDownloadProgress(
                        new Progress<WinPeDownloadProgress>(ReportDownloadProgress)),
                    Progress = telemetryProgressTracker.CreateFinalMediaProgress(
                        new Progress<WinPeMediaProgress>(ReportFinalMediaProgress))
                },
                workspace.PreparedWorkspace.Artifact,
                workspace.Tools,
                workspace.PreparedWorkspace.UseBootEx,
                cancellationToken);

            EnsureSuccess(result);
            logger.Debug(
                "USB media service completed. DiskNumber={DiskNumber}, BootVolume={BootVolume}, CacheVolume={CacheVolume}",
                selectedDisk.DiskNumber,
                result.Value?.BootDriveLetter,
                result.Value?.CacheDriveLetter);
            return result.Value!;
        }
        finally
        {
            CleanupPreparedWorkspace(workspace?.PreparedWorkspace.Artifact.WorkingDirectoryPath);
        }
    }

    private async Task<WinPeUsbProvisionResult> UpdateUsbMediaAsync(
        MediaPreflightOptions options,
        DeploymentMediaProtectionMaterial deploymentProtectionMaterial,
        MediaCreationTelemetryProgressTracker telemetryProgressTracker,
        CancellationToken cancellationToken)
    {
        telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.ValidateUsbTarget);
        if (options.SelectedUsbDisk is null)
        {
            throw new InvalidOperationException(localizationService.GetString("StartMedia.BlockingReason.NoUsbTarget"));
        }

        PreparedMediaWorkspace? workspace = null;

        try
        {
            workspace = await PrepareMediaWorkspaceAsync(
                options,
                includeRuntimePayloadInImage: false,
                deploymentProtectionMaterial,
                telemetryProgressTracker: telemetryProgressTracker,
                cancellationToken);

            WinPeUsbDiskCandidate selectedDisk = options.SelectedUsbDisk;
            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.UpdateUsbMedia);
            logger.Debug(
                "Updating USB boot partition. DiskNumber={DiskNumber}, DiskName={DiskName}, FormatMode={FormatMode}, MediaDirectoryPath={MediaDirectoryPath}, UseBootEx={UseBootEx}",
                selectedDisk.DiskNumber,
                selectedDisk.FriendlyName,
                options.UsbFormatMode,
                workspace.PreparedWorkspace.Artifact.MediaDirectoryPath,
                workspace.PreparedWorkspace.UseBootEx);

            WinPeResult<WinPeUsbProvisionResult> result = await usbMediaService.UpdateBootPartitionAsync(
                new UsbOutputOptions
                {
                    TargetDiskNumber = selectedDisk.DiskNumber,
                    ExpectedDiskFriendlyName = selectedDisk.FriendlyName,
                    ExpectedDiskSerialNumber = selectedDisk.SerialNumber,
                    ExpectedDiskUniqueId = selectedDisk.UniqueId,
                    FormatMode = options.UsbFormatMode,
                    RuntimePayloadProvisioning = workspace.RuntimePayloadProvisioning,
                    DownloadProgress = telemetryProgressTracker.CreateDownloadProgress(
                        new Progress<WinPeDownloadProgress>(ReportDownloadProgress)),
                    Progress = telemetryProgressTracker.CreateFinalMediaProgress(
                        new Progress<WinPeMediaProgress>(ReportFinalMediaProgress))
                },
                workspace.PreparedWorkspace.Artifact,
                workspace.Tools,
                workspace.PreparedWorkspace.UseBootEx,
                cancellationToken);

            EnsureSuccess(result);
            logger.Debug(
                "USB boot partition update service completed. DiskNumber={DiskNumber}, BootVolume={BootVolume}, CacheVolume={CacheVolume}",
                selectedDisk.DiskNumber,
                result.Value?.BootDriveLetter,
                result.Value?.CacheDriveLetter);
            return result.Value!;
        }
        finally
        {
            CleanupPreparedWorkspace(workspace?.PreparedWorkspace.Artifact.WorkingDirectoryPath);
        }
    }

    private async Task<PreparedMediaWorkspace> PrepareMediaWorkspaceAsync(
        MediaPreflightOptions options,
        bool includeRuntimePayloadInImage,
        DeploymentMediaProtectionMaterial deploymentProtectionMaterial,
        MediaCreationTelemetryProgressTracker telemetryProgressTracker,
        CancellationToken cancellationToken)
    {
        WinPeBuildArtifact? artifact = null;

        try
        {
            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.ResolveWinPeTools);
            WinPeToolPaths tools = ResolveWinPeToolsOrThrow();
            logger.Debug(
                "Resolved WinPE tools. KitsRootPath={KitsRootPath}, DismPath={DismPath}, MakeWinPeMediaPath={MakeWinPeMediaPath}",
                tools.KitsRootPath,
                tools.DismPath,
                tools.MakeWinPeMediaPath);

            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.PrepareRuntimePayloads);
            WinPeRuntimePayloadProvisioningOptions runtimePayloadProvisioning = CreateRuntimePayloadProvisioningOptions(
                options.Architecture,
                Constants.WinPeWorkspaceDirectoryPath,
                Constants.WinPeWorkspaceDirectoryPath,
                Constants.WinPeWorkspaceDirectoryPath);
            runtimePayloadProvisioning = AddReleaseConnectProvisioning(runtimePayloadProvisioning);
            TelemetrySettings connectTelemetrySettings = CreateRuntimeTelemetrySettings(ResolveRuntimePayloadSource(runtimePayloadProvisioning.Connect));
            TelemetrySettings deployTelemetrySettings = CreateRuntimeTelemetrySettings(ResolveRuntimePayloadSource(runtimePayloadProvisioning.Deploy));

            logger.Debug(
                "Final media workspace preparation started. Architecture={Architecture}, WinPeLanguage={WinPeLanguage}, SignatureMode={SignatureMode}, BootImageSource={BootImageSource}, IncludeRuntimePayloadInImage={IncludeRuntimePayloadInImage}, DriverVendorCount={DriverVendorCount}, HasCustomDriverDirectory={HasCustomDriverDirectory}, IsAutopilotEnabled={IsAutopilotEnabled}, IsConnectRuntimeProvisioningEnabled={IsConnectRuntimeProvisioningEnabled}, ConnectRuntimeSource={ConnectRuntimeSource}, IsDeployRuntimeProvisioningEnabled={IsDeployRuntimeProvisioningEnabled}, DeployRuntimeSource={DeployRuntimeSource}",
                options.Architecture,
                NormalizeCultureName(options.WinPeLanguage),
                options.SignatureMode,
                options.BootImageSource,
                includeRuntimePayloadInImage,
                options.DriverVendors.Count,
                !string.IsNullOrWhiteSpace(options.CustomDriverDirectoryPath),
                options.IsAutopilotEnabled,
                runtimePayloadProvisioning.Connect.IsEnabled,
                ResolveProvisioningSource(runtimePayloadProvisioning.Connect),
                runtimePayloadProvisioning.Deploy.IsEnabled,
                ResolveProvisioningSource(runtimePayloadProvisioning.Deploy));

            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.CleanStaleWorkspaces);
            CleanupStaleWinPeWorkspaces();

            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.BuildWinPeWorkspace);
            operationProgressService.Report(10, localizationService.GetString("StartMedia.Operation.BuildingWorkspace"));
            WinPeResult<WinPeBuildArtifact> buildResult = await buildService.BuildAsync(
                new WinPeBuildOptions
                {
                    OutputDirectoryPath = Constants.WinPeWorkspaceDirectoryPath,
                    AdkRootPath = tools.KitsRootPath,
                    Architecture = options.Architecture,
                    SignatureMode = options.SignatureMode
                },
                cancellationToken);
            EnsureSuccess(buildResult);

            artifact = buildResult.Value!;
            logger.Debug(
                "WinPE workspace created. WorkingDirectoryPath={WorkingDirectoryPath}, MediaDirectoryPath={MediaDirectoryPath}, MountDirectoryPath={MountDirectoryPath}, BootWimPath={BootWimPath}",
                artifact.WorkingDirectoryPath,
                artifact.MediaDirectoryPath,
                artifact.MountDirectoryPath,
                artifact.BootWimPath);

            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.GenerateProvisioningPayloads);
            FoundryConnectProvisioningBundle connectBundle = foundryConfigurationStateService.GenerateConnectProvisioningBundle(
                Path.Combine(artifact.WorkingDirectoryPath, "Provisioning"),
                connectTelemetrySettings);
            logger.Debug(
                "Generated local provisioning payloads. ConnectAssetFileCount={ConnectAssetFileCount}, HasMediaSecretsKey={HasMediaSecretsKey}, AutopilotProfileCount={AutopilotProfileCount}",
                connectBundle.AssetFiles.Count,
                connectBundle.MediaSecretsKey is { Length: > 0 },
                options.IsAutopilotEnabled ? foundryConfigurationStateService.Current.Autopilot.Profiles.Count : 0);

            WinPeRuntimePayloadProvisioningOptions artifactRuntimePayloadProvisioning = runtimePayloadProvisioning with
            {
                WorkingDirectoryPath = artifact.WorkingDirectoryPath,
                MountedImagePath = artifact.MountDirectoryPath,
                UsbCacheRootPath = string.Empty
            };

            telemetryProgressTracker.SetCurrentStep(MediaCreationStepNames.PrepareWinPeWorkspace);
            IProgress<WinPeWorkspacePreparationStage> workspacePreparationProgress =
                telemetryProgressTracker.CreateWorkspacePreparationProgress(
                    new Progress<WinPeWorkspacePreparationStage>(ReportWorkspacePreparationStage));

            operationProgressService.Report(25, localizationService.GetString("StartMedia.Operation.PreparingWorkspace"));
            WinPeResult<WinPeWorkspacePreparationResult> preparationResult;
            try
            {
                preparationResult = await workspacePreparationService.PrepareAsync(
                    new WinPeWorkspacePreparationOptions
                    {
                        Artifact = artifact,
                        Tools = tools,
                        SignatureMode = options.SignatureMode,
                        BootImageSource = options.BootImageSource,
                        DriverCatalogUri = new WinPeDriverCatalogOptions().CatalogUri,
                        DriverVendors = options.DriverVendors,
                        CustomDriverDirectoryPath = options.CustomDriverDirectoryPath,
                        WinPeLanguage = options.WinPeLanguage,
                        AssetProvisioning = CreateAssetProvisioningOptions(
                            options,
                            tools,
                            connectBundle,
                            deploymentProtectionMaterial,
                            runtimePayloadProvisioning,
                            deployTelemetrySettings),
                        RuntimePayloadProvisioning = includeRuntimePayloadInImage ? artifactRuntimePayloadProvisioning : null,
                        WinReCacheDirectoryPath = Constants.WinReTempDirectoryPath,
                        Progress = workspacePreparationProgress,
                        DownloadProgress = telemetryProgressTracker.CreateDownloadProgress(
                            new Progress<WinPeDownloadProgress>(ReportDownloadProgress)),
                        CustomizationProgress = telemetryProgressTracker.CreateCustomizationProgress(
                            new Progress<WinPeMountedImageCustomizationProgress>(ReportCustomizationProgress))
                    },
                    cancellationToken);
            }
            finally
            {
                if (connectBundle.MediaSecretsKey is not null)
                {
                    CryptographicOperations.ZeroMemory(connectBundle.MediaSecretsKey);
                }
            }
            EnsureSuccess(preparationResult);

            logger.Debug(
                "WinPE workspace prepared. UseBootEx={UseBootEx}, RuntimePayloadInImage={RuntimePayloadInImage}",
                preparationResult.Value!.UseBootEx,
                includeRuntimePayloadInImage);

            return new PreparedMediaWorkspace(
                preparationResult.Value!,
                tools,
                artifactRuntimePayloadProvisioning with
                {
                    MountedImagePath = string.Empty,
                    UsbCacheRootPath = string.Empty
                });
        }
        catch
        {
            CleanupPreparedWorkspace(artifact?.WorkingDirectoryPath);
            throw;
        }
    }

    private WinPeMountedImageAssetProvisioningOptions CreateAssetProvisioningOptions(
        MediaPreflightOptions options,
        WinPeToolPaths tools,
        FoundryConnectProvisioningBundle connectBundle,
        DeploymentMediaProtectionMaterial deploymentProtectionMaterial,
        WinPeRuntimePayloadProvisioningOptions runtimePayloadProvisioning,
        TelemetrySettings deployTelemetrySettings)
    {
        bool isHardwareHashMode = options.IsAutopilotEnabled &&
                                  options.AutopilotProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload;
        string? oa3ToolSourcePath = null;
        if (isHardwareHashMode)
        {
            WinPeResult<string> oa3ToolResult = WinPeOa3ToolResolver.Resolve(tools.KitsRootPath, options.Architecture);
            EnsureSuccess(oa3ToolResult);
            oa3ToolSourcePath = oa3ToolResult.Value;
        }

        return new WinPeMountedImageAssetProvisioningOptions
        {
            BootstrapScriptContent = embeddedAssetService.GetBootstrapScriptContent(),
            CurlExecutableSourcePath = ResolveCurlExecutablePath(),
            SevenZipSourceDirectoryPath = embeddedAssetService.GetSevenZipSourceDirectoryPath(),
            IanaWindowsTimeZoneMapJson = embeddedAssetService.GetIanaWindowsTimeZoneMapJson(),
            FoundryConnectConfigurationJson = connectBundle.ConfigurationJson,
            DeployConfigurationJson = foundryConfigurationStateService.GenerateDeployConfigurationJson(
                deployTelemetrySettings,
                deploymentProtectionMaterial.DeploymentKey,
                deploymentProtectionMaterial.Settings),
            NetworkSecretsKey = connectBundle.MediaSecretsKey,
            DeploymentSecretsKey = deploymentProtectionMaterial.DeploymentKey,
            IsDeploymentProtectionEnabled = deploymentProtectionMaterial.Settings.IsEnabled,
            Unattend = foundryConfigurationStateService.Current.Unattend,
            FoundryConnectAssetFiles = connectBundle.AssetFiles,
            AutopilotProvisioningMode = options.IsAutopilotEnabled
                ? options.AutopilotProvisioningMode
                : AutopilotProvisioningMode.JsonProfile,
            Oa3ToolSourcePath = oa3ToolSourcePath,
            AutopilotProfiles = options.IsAutopilotEnabled && options.AutopilotProvisioningMode == AutopilotProvisioningMode.JsonProfile
                ? foundryConfigurationStateService.Current.Autopilot.Profiles
                : [],
            ConnectProvisioningSource = ResolveProvisioningSource(runtimePayloadProvisioning.Connect),
            DeployProvisioningSource = ResolveProvisioningSource(runtimePayloadProvisioning.Deploy)
        };
    }

    private DeploymentMediaProtectionMaterial CreateDeploymentProtectionMaterial()
    {
        if (!foundryConfigurationStateService.Current.General.DeploymentProtection.IsEnabled)
        {
            return DeploymentMediaProtectionService.CreateUnprotected();
        }

        char[] password = deploymentProtectionSecretStateService.GetConfirmedPasswordCopy();
        try
        {
            return DeploymentMediaProtectionService.CreateProtected(password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
        }
    }

    private static WinPeProvisioningSource ResolveProvisioningSource(WinPeRuntimePayloadApplicationOptions options)
    {
        return options.IsEnabled ? options.ProvisioningSource : WinPeProvisioningSource.Release;
    }

    private static string ResolveRuntimePayloadSource(WinPeRuntimePayloadApplicationOptions options)
    {
        return ResolveProvisioningSource(options) switch
        {
            WinPeProvisioningSource.Debug => TelemetryRuntimePayloadSources.Debug,
            WinPeProvisioningSource.Release => TelemetryRuntimePayloadSources.Release,
            _ => TelemetryRuntimePayloadSources.Unknown
        };
    }

    private void ReportWorkspacePreparationStage(WinPeWorkspacePreparationStage stage)
    {
        int progress = stage switch
        {
            WinPeWorkspacePreparationStage.ResolvingDrivers => 30,
            WinPeWorkspacePreparationStage.CustomizingImage => 35,
            WinPeWorkspacePreparationStage.EvaluatingSignaturePolicy => 84,
            _ => 35
        };

        string statusResourceKey = stage switch
        {
            WinPeWorkspacePreparationStage.ResolvingDrivers => "StartMedia.Operation.ResolvingDrivers",
            WinPeWorkspacePreparationStage.CustomizingImage => "StartMedia.Operation.CustomizingImage",
            WinPeWorkspacePreparationStage.EvaluatingSignaturePolicy => "StartMedia.Operation.EvaluatingSignaturePolicy",
            _ => "StartMedia.Operation.PreparingWorkspace"
        };

        logger.Debug("WinPE workspace preparation stage changed. Stage={Stage}, Progress={Progress}", stage, progress);
        operationProgressService.Report(progress, localizationService.GetString(statusResourceKey));
    }

    private void ReportCustomizationProgress(WinPeMountedImageCustomizationProgress progress)
    {
        int normalizedProgress = Math.Clamp(35 + (progress.Percent * 47 / 100), 35, 82);
        string status = string.IsNullOrWhiteSpace(progress.Status)
            ? localizationService.GetString("StartMedia.Operation.PreparingWorkspace")
            : LocalizeCustomizationStatus(progress.Status);

        int sampledPercent = progress.DetailPercent ?? progress.Percent;
        string sampledStatus = $"{progress.Status}|{progress.DetailStatus}";
        if (ShouldLogProgress(ref lastCustomizationLogStatus, ref lastCustomizationLogBucket, sampledStatus, sampledPercent))
        {
            logger.Debug(
                "WinPE image customization progress changed. CoreStatus={CoreStatus}, Percent={Percent}, NormalizedProgress={NormalizedProgress}, DetailStatus={DetailStatus}, DetailPercent={DetailPercent}",
                progress.Status,
                progress.Percent,
                normalizedProgress,
                progress.DetailStatus,
                progress.DetailPercent);
        }

        if (progress.DetailPercent.HasValue || !string.IsNullOrWhiteSpace(progress.DetailStatus))
        {
            operationProgressService.Report(
                normalizedProgress,
                status,
                progress.DetailPercent,
                LocalizeDismProgressStatus(progress.DetailStatus));
            return;
        }

        operationProgressService.Report(normalizedProgress, status);
    }

    private void ReportDownloadProgress(WinPeDownloadProgress progress)
    {
        string status = string.IsNullOrWhiteSpace(progress.Status)
            ? localizationService.GetString("StartMedia.Operation.Downloading")
            : LocalizeDownloadStatus(progress.Status);

        if (ShouldLogProgress(ref lastDownloadLogStatus, ref lastDownloadLogBucket, progress.Status, progress.Percent))
        {
            logger.Debug(
                "Final media download progress changed. DownloadStatus={DownloadStatus}, DownloadPercent={DownloadPercent}",
                progress.Status,
                progress.Percent);
        }
        operationProgressService.Report(
            operationProgressService.State.Progress,
            operationProgressService.State.Status,
            progress.Percent,
            status);
    }

    private void ReportFinalMediaProgress(WinPeMediaProgress progress)
    {
        int normalizedProgress = Math.Clamp(88 + (progress.Percent * 10 / 100), 88, 98);
        string status = string.IsNullOrWhiteSpace(progress.Status)
            ? localizationService.GetString("StartMedia.Operation.CreatingIso")
            : LocalizeFinalMediaStatus(progress.Status);

        if (ShouldLogProgress(ref lastFinalMediaLogStatus, ref lastFinalMediaLogBucket, progress.Status, progress.Percent))
        {
            logger.Debug(
                "Final media output progress changed. CoreStatus={CoreStatus}, Percent={Percent}, NormalizedProgress={NormalizedProgress}, HasLogDetail={HasLogDetail}",
                progress.Status,
                progress.Percent,
                normalizedProgress,
                !string.IsNullOrWhiteSpace(progress.LogDetail));
        }
        operationProgressService.Report(normalizedProgress, status);
    }

    private void ResetProgressLogSampling()
    {
        lastCustomizationLogStatus = null;
        lastCustomizationLogBucket = -1;
        lastDownloadLogStatus = null;
        lastDownloadLogBucket = -1;
        lastFinalMediaLogStatus = null;
        lastFinalMediaLogBucket = -1;
    }

    private static bool ShouldLogProgress(ref string? previousStatus, ref int previousBucket, string? status, int? percent)
    {
        int bucket = percent.HasValue ? Math.Clamp(percent.Value, 0, 100) / 10 : -2;
        bool shouldLog = !string.Equals(previousStatus, status, StringComparison.Ordinal) || previousBucket != bucket;
        if (shouldLog)
        {
            previousStatus = status;
            previousBucket = bucket;
        }

        return shouldLog;
    }

    private string LocalizeCustomizationStatus(string status)
    {
        string resourceKey = status switch
        {
            "Preparing boot image customization." => "StartMedia.Operation.PreparingBootImageCustomization",
            "Mounting boot image." => "StartMedia.Operation.MountingBootImage",
            "Injecting drivers into mounted image." => "StartMedia.Operation.InjectingDrivers",
            "Applying language and optional components." => "StartMedia.Operation.ApplyingLanguageAndComponents",
            "Provisioning Foundry boot assets." => "StartMedia.Operation.ProvisioningBootAssets",
            "Provisioning Foundry runtime payloads." => "StartMedia.Operation.ProvisioningRuntimePayloads",
            "Committing image changes." => "StartMedia.Operation.CommittingImageChanges",
            "Image customization completed." => "StartMedia.Operation.ImageCustomizationCompleted",
            "Resolving WinRE source catalog." => "StartMedia.Operation.ResolvingWinReSourceCatalog",
            "Selected WinRE source package." => "StartMedia.Operation.SelectedWinReSourcePackage",
            "Preparing WinRE source package." => "StartMedia.Operation.PreparingWinReSourcePackage",
            "Validating cached WinRE source package." => "StartMedia.Operation.ValidatingCachedWinReSourcePackage",
            "Using cached WinRE source package." => "StartMedia.Operation.UsingCachedWinReSourcePackage",
            "Downloading WinRE source package." => "StartMedia.Operation.DownloadingWinReSourcePackage",
            "Validating WinRE source package." => "StartMedia.Operation.ValidatingWinReSourcePackage",
            "Resolving WinRE image index." => "StartMedia.Operation.ResolvingWinReImageIndex",
            "Exporting Windows image for WinRE extraction." => "StartMedia.Operation.ExportingWinReSourceImage",
            "Mounting WinRE source image." => "StartMedia.Operation.MountingWinReSourceImage",
            "Staging WinRE Wi-Fi dependencies." => "StartMedia.Operation.StagingWinReWifiDependencies",
            "Replacing boot image with WinRE." => "StartMedia.Operation.ReplacingBootImageWithWinRe",
            "WinRE Wi-Fi boot image is ready." => "StartMedia.Operation.WinReWifiBootImageReady",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(resourceKey)
            ? status
            : localizationService.GetString(resourceKey);
    }

    private string LocalizeDownloadStatus(string status)
    {
        if (status.StartsWith("Downloading WinRE source package", StringComparison.Ordinal))
        {
            int suffixIndex = status.IndexOf('(', StringComparison.Ordinal);
            string suffix = suffixIndex >= 0
                ? $" {status[suffixIndex..]}"
                : string.Empty;
            return localizationService.GetString("StartMedia.Operation.DownloadingWinReSourcePackage") + suffix;
        }

        if (status.StartsWith("Downloading driver package", StringComparison.Ordinal))
        {
            int suffixIndex = status.IndexOf(':', StringComparison.Ordinal);
            string suffix = suffixIndex >= 0
                ? status[suffixIndex..]
                : string.Empty;
            return localizationService.GetString("StartMedia.Operation.DownloadingDriverPackage") + suffix;
        }

        const string runtimeDownloadPrefix = "Downloading ";
        const string runtimeDownloadSuffix = " runtime payload.";
        if (status.StartsWith(runtimeDownloadPrefix, StringComparison.Ordinal))
        {
            int suffixIndex = status.IndexOf('(', StringComparison.Ordinal);
            int applicationNameEndIndex = status.IndexOf(runtimeDownloadSuffix, StringComparison.Ordinal);
            if (applicationNameEndIndex > runtimeDownloadPrefix.Length)
            {
                string applicationName = status[runtimeDownloadPrefix.Length..applicationNameEndIndex];
                string suffix = suffixIndex >= 0
                    ? $" {status[suffixIndex..]}"
                    : string.Empty;
                return localizationService.FormatString("StartMedia.Operation.DownloadingRuntimePayloadFormat", applicationName) + suffix;
            }
        }

        return status;
    }

    private string LocalizeDismProgressStatus(string status)
    {
        string resourceKey = status switch
        {
            "Exporting Windows image with DISM." => "StartMedia.Operation.DismExportingWindowsImage",
            "Resolving WinRE image index with DISM." => "StartMedia.Operation.DismResolvingWinReImageIndex",
            "Mounting image with DISM." => "StartMedia.Operation.DismMountingImage",
            "Injecting drivers with DISM." => "StartMedia.Operation.DismInjectingDrivers",
            "Applying language pack with DISM." => "StartMedia.Operation.DismApplyingLanguagePack",
            "Applying optional components with DISM." => "StartMedia.Operation.DismApplyingOptionalComponents",
            "Applying international settings with DISM." => "StartMedia.Operation.DismApplyingInternationalSettings",
            "Committing image changes with DISM." => "StartMedia.Operation.DismCommittingImageChanges",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(resourceKey)
            ? status
            : localizationService.GetString(resourceKey);
    }

    private string LocalizeFinalMediaStatus(string status)
    {
        string resourceKey = status switch
        {
            "Preparing ISO output path." => "StartMedia.Operation.PreparingIsoOutput",
            "Preparing ISO workspace." => "StartMedia.Operation.PreparingIsoWorkspace",
            "Running MakeWinPEMedia for ISO." => "StartMedia.Operation.RunningMakeWinPeMediaIso",
            "Finalizing ISO output." => "StartMedia.Operation.FinalizingIsoOutput",
            "ISO media completed." => "StartMedia.Operation.IsoMediaCompleted",
            "Validating USB target." => "StartMedia.Operation.ValidatingUsbTarget",
            "Checking USB target safety." => "StartMedia.Operation.CheckingUsbTargetSafety",
            "Inspecting USB media layout." => "StartMedia.Operation.InspectingUsbMediaLayout",
            "Partitioning and formatting USB target." => "StartMedia.Operation.PartitioningUsbTarget",
            "Opening USB disk." => "StartMedia.Operation.OpeningUsbDisk",
            "Preparing USB disk attributes." => "StartMedia.Operation.PreparingUsbDiskAttributes",
            "Clearing USB partition table." => "StartMedia.Operation.ClearingUsbPartitionTable",
            "Initializing USB partition table." => "StartMedia.Operation.InitializingUsbPartitionTable",
            "Creating BOOT partition." => "StartMedia.Operation.CreatingUsbBootPartition",
            "Formatting BOOT partition." => "StartMedia.Operation.FormattingUsbBootPartition",
            "Creating cache partition." => "StartMedia.Operation.CreatingUsbCachePartition",
            "Formatting cache partition." => "StartMedia.Operation.FormattingUsbCachePartition",
            "USB partitions formatted." => "StartMedia.Operation.UsbPartitionsFormatted",
            "Copying WinPE media to USB." => "StartMedia.Operation.CopyingUsbMedia",
            "Configuring USB boot files." => "StartMedia.Operation.ConfiguringUsbBootFiles",
            "Verifying USB boot media." => "StartMedia.Operation.VerifyingUsbMedia",
            "Preparing USB cache partition." => "StartMedia.Operation.PreparingUsbCache",
            "Provisioning USB runtime payloads." => "StartMedia.Operation.ProvisioningUsbRuntimePayloads",
            "USB media completed." => "StartMedia.Operation.UsbMediaCompleted",
            "USB boot partition updated." => "StartMedia.Operation.UsbBootPartitionUpdated",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(resourceKey)
            ? status
            : localizationService.GetString(resourceKey);
    }

    private void CleanupPreparedWorkspace(string? workingDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(workingDirectoryPath))
        {
            return;
        }

        string workspaceRoot = Path.GetFullPath(Constants.WinPeWorkspaceDirectoryPath);
        string workspacePath = Path.GetFullPath(workingDirectoryPath);
        string normalizedRoot = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!workspacePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            logger.Warning(
                "Skipped WinPE workspace cleanup because the target is outside the workspace root. WorkspacePath={WorkspacePath}, WorkspaceRoot={WorkspaceRoot}",
                workspacePath,
                workspaceRoot);
            return;
        }

        if (!Directory.Exists(workspacePath))
        {
            logger.Debug("Skipped WinPE workspace cleanup because the directory no longer exists. WorkspacePath={WorkspacePath}", workspacePath);
            return;
        }

        DeleteWorkspaceDirectory(workspacePath, reportProgress: true);
    }

    private void CleanupStaleWinPeWorkspaces()
    {
        string workspaceRoot = Path.GetFullPath(Constants.WinPeWorkspaceDirectoryPath);
        if (!Directory.Exists(workspaceRoot))
        {
            return;
        }

        foreach (string workspacePath in Directory.EnumerateDirectories(workspaceRoot))
        {
            DeleteWorkspaceDirectory(workspacePath, reportProgress: false);
        }

        foreach (string filePath in Directory.EnumerateFiles(workspaceRoot))
        {
            try
            {
                logger.Debug("Cleaning stale WinPE workspace file. FilePath={FilePath}", filePath);
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Failed to clean stale WinPE workspace file. FilePath={FilePath}", filePath);
            }
        }
    }

    private void DeleteWorkspaceDirectory(string workspacePath, bool reportProgress)
    {
        try
        {
            if (reportProgress)
            {
                operationProgressService.Report(99, localizationService.GetString("StartMedia.Operation.CleaningWorkspace"));
            }

            logger.Debug("Cleaning WinPE workspace. WorkspacePath={WorkspacePath}", workspacePath);
            NormalizeWorkspaceAttributes(workspacePath);
            Directory.Delete(workspacePath, recursive: true);
            logger.Debug("WinPE workspace cleaned. WorkspacePath={WorkspacePath}", workspacePath);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Failed to clean WinPE workspace. WorkspacePath={WorkspacePath}", workspacePath);
        }
    }

    private static void NormalizeWorkspaceAttributes(string workspacePath)
    {
        foreach (string filePath in Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }

        foreach (string directoryPath in Directory
            .EnumerateDirectories(workspacePath, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            File.SetAttributes(directoryPath, FileAttributes.Directory);
        }

        File.SetAttributes(workspacePath, FileAttributes.Directory);
    }

    private async Task ShowBlockedDialogAsync(
        string titleResourceKey,
        IReadOnlyList<MediaPreflightBlockingReason> blockingReasons)
    {
        IReadOnlyList<MediaPreflightBlockingReason> reasons = blockingReasons.Distinct().ToList();
        string message = reasons.Count == 0
            ? localizationService.GetString("StartMedia.Operation.Failed")
            : string.Join(Environment.NewLine, reasons.Select(reason => $"- {GetBlockingReasonText(reason)}"));

        await dialogService.ShowMessageAsync(new DialogRequest(
            localizationService.GetString(titleResourceKey),
            message,
            localizationService.GetString("Common.Close")));
    }

    private async Task RefreshUnattendSourcesAsync()
    {
        if (isDisposed || isRefreshingUnattendSources || !foundryConfigurationStateService.Current.Unattend.IsEnabled)
        {
            return;
        }

        isRefreshingUnattendSources = true;
        RefreshEvaluation();
        try
        {
            await foundryConfigurationStateService.RefreshUnattendSourcesAsync();
        }
        finally
        {
            isRefreshingUnattendSources = false;
            if (!isDisposed)
            {
                RefreshEvaluation();
            }
        }
    }

    partial void OnSelectedUsbDiskChanged(SelectionOption<WinPeUsbDiskCandidate>? value)
    {
        IsSelectedUsbFoundryMedia = value?.Value.IsFoundryMedia == true;
        RefreshEvaluation();
    }

    partial void OnIsoOutputPathChanged(string value)
    {
        if (isLoadingConfiguration)
        {
            return;
        }

        UpdateGeneral(foundryConfigurationStateService.Current.General with { IsoOutputPath = value });
        RefreshEvaluation();
    }

    partial void OnSelectedPartitionStyleChanged(SelectionOption<UsbPartitionStyle>? value)
    {
        if (value is null || isLoadingConfiguration)
        {
            return;
        }

        if (SelectedArchitecture?.Value == WinPeArchitecture.Arm64 && value.Value == UsbPartitionStyle.Mbr)
        {
            SelectedPartitionStyle = SelectOption(PartitionStyles, UsbPartitionStyle.Gpt);
            return;
        }

        UpdateGeneral(foundryConfigurationStateService.Current.General with { UsbPartitionStyle = value.Value });
        RefreshEvaluation(loadConfigurationFromSettings: false);
    }

    partial void OnSelectedFormatModeChanged(SelectionOption<UsbFormatMode>? value)
    {
        if (value is null || isLoadingConfiguration)
        {
            return;
        }

        UpdateGeneral(foundryConfigurationStateService.Current.General with { UsbFormatMode = value.Value });
        RefreshEvaluation();
    }

    private void OnConfigurationOverviewChanged(object? sender, EventArgs e)
    {
        if (!appDispatcher.TryEnqueue(() => RefreshEvaluation()))
        {
            logger.Warning("Failed to enqueue Start page configuration overview refresh.");
        }
    }

    private void OnLanguageChanged(object? sender, ApplicationLanguageChangedEventArgs e)
    {
        if (!appDispatcher.TryEnqueue(() =>
        {
            ApplyLocalizedText();
            RefreshEvaluation();
        }))
        {
            logger.Warning(
                "Failed to enqueue Start page localization refresh. OldLanguage={OldLanguage}, NewLanguage={NewLanguage}",
                e.OldLanguage,
                e.NewLanguage);
        }
    }

    private void ApplyLocalizedText()
    {
        PageTitle = localizationService.GetString("StartPage_Title.Text");
        PageDescription = localizationService.GetString("StartMedia.PageDescription");
        RebuildFormatModes();
        RebuildUsbCandidateDisplayNames();
    }

    private void RefreshEvaluation(bool loadConfigurationFromSettings = true)
    {
        if (loadConfigurationFromSettings)
        {
            isLoadingConfiguration = true;
            try
            {
                LoadConfigurationFromSettings();
            }
            finally
            {
                isLoadingConfiguration = false;
            }
        }

        WinPeLanguage = NormalizeCultureName(foundryConfigurationStateService.Current.General.WinPeLanguage);
        availableWinPeLanguages = GetAvailableWinPeLanguages();
        MediaPreflightOptions options = CreatePreflightOptions();
        MediaPreflightEvaluation evaluation = MediaPreflightService.Evaluate(options);
        IsSelectedUsbFoundryMedia = options.SelectedUsbDisk?.IsFoundryMedia == true;
        CanGenerateIsoSummary = evaluation.CanGenerateIsoSummary;
        CanGenerateUsbSummary = evaluation.CanGenerateUsbSummary;
        CanSelectUsbDisk = UsbCandidates.Count > 0 && !IsRefreshingUsbCandidates;
        CanCreateIso = evaluation.CanCreateIso && !IsMediaOperationRunning && !isRefreshingUnattendSources;
        CanCreateUsb = evaluation.CanCreateUsb && !IsMediaOperationRunning && !isRefreshingUnattendSources;
        FinalExecutionStatus = options.IsFinalExecutionEnabled
            ? localizationService.GetString("StartMedia.FinalExecution.Ready")
            : localizationService.GetString("StartMedia.FinalExecution.Deferred");
        ConfigurationOverviewEvaluation overview = configurationOverviewService.Evaluate();
        ReadinessSeverity = GetReadinessSeverity(evaluation);
        StatusSummary = BuildStatusText(evaluation, overview);
        GlobalSummary = BuildGlobalSummary(options, evaluation);
        RebuildConfigurationOverview(options, evaluation, overview);

    }

    private void LoadConfigurationFromSettings()
    {
        GeneralSettings general = foundryConfigurationStateService.Current.General;
        IsoOutputPath = general.IsoOutputPath ?? string.Empty;
        SelectedArchitecture = SelectOption(Architectures, general.Architecture);
        UseCa2023Signature = general.UseCa2023;
        RebuildPartitionStyles();
        SelectedFormatMode = SelectOption(FormatModes, general.UsbFormatMode);
        IncludeDellDrivers = general.IncludeDellDrivers;
        IncludeHpDrivers = general.IncludeHpDrivers;
        CustomDriverDirectoryPath = general.CustomDriverDirectoryPath ?? string.Empty;
    }

    private MediaPreflightOptions CreatePreflightOptions()
    {
        List<WinPeVendorSelection> vendors = [];
        if (IncludeDellDrivers)
        {
            vendors.Add(WinPeVendorSelection.Dell);
        }

        if (IncludeHpDrivers)
        {
            vendors.Add(WinPeVendorSelection.Hp);
        }

        AutopilotConfigurationValidationResult autopilotValidation = foundryConfigurationStateService.AutopilotConfigurationValidation;
        NetworkMediaReadinessEvaluation networkReadiness = foundryConfigurationStateService.NetworkMediaReadiness;

        return new MediaPreflightOptions
        {
            IsAdkReady = adkService.CurrentStatus.CanCreateMedia,
            IsNetworkConfigurationReady = networkReadiness.IsNetworkConfigurationReady,
            NetworkConfigurationValidationCode = networkReadiness.NetworkConfigurationValidationCode,
            IsDeployConfigurationReady = foundryConfigurationStateService.IsDeployConfigurationReady,
            IsConnectProvisioningReady = networkReadiness.IsConnectProvisioningReady,
            AreRequiredSecretsReady = networkReadiness.AreRequiredSecretsReady,
            IsAutopilotEnabled = foundryConfigurationStateService.IsAutopilotEnabled,
            IsAutopilotConfigurationReady = autopilotValidation.IsReady,
            AutopilotConfigurationValidationCode = autopilotValidation.Code,
            AutopilotProvisioningMode = foundryConfigurationStateService.AutopilotProvisioningMode,
            AutopilotProfileDisplayName = foundryConfigurationStateService.SelectedAutopilotProfileDisplayName,
            AutopilotProfileFolderName = foundryConfigurationStateService.SelectedAutopilotProfileFolderName,
            IsFinalExecutionEnabled = true,
            IsoOutputPath = IsoOutputPath,
            Architecture = SelectedArchitecture?.Value ?? WinPeArchitecture.X64,
            SignatureMode = UseCa2023Signature ? WinPeSignatureMode.Pca2023 : WinPeSignatureMode.Pca2011,
            UsbPartitionStyle = SelectedPartitionStyle?.Value ?? UsbPartitionStyle.Gpt,
            UsbFormatMode = SelectedFormatMode?.Value ?? UsbFormatMode.Quick,
            WinPeLanguage = WinPeLanguage,
            AvailableWinPeLanguages = availableWinPeLanguages,
            BootImageSource = ResolveBootImageSource(),
            DriverVendors = vendors,
            CustomDriverDirectoryPath = CustomDriverDirectoryPath,
            SelectedUsbDisk = SelectedUsbDisk?.Value
        };
    }

    private void UpdateGeneral(GeneralSettings settings)
    {
        foundryConfigurationStateService.UpdateGeneral(settings);
    }

    private void SynchronizeRuntimeTelemetrySettings()
    {
        foundryConfigurationStateService.UpdateTelemetry(CreateRuntimeTelemetrySettings(TelemetryRuntimePayloadSources.None));
    }

    private TelemetrySettings CreateRuntimeTelemetrySettings(string runtimePayloadSource)
    {
        return new TelemetrySettings
        {
            IsEnabled = appSettingsService.Current.Telemetry.IsEnabled,
            IsRemoteDiagnosticsEnabled = appSettingsService.Current.Telemetry.IsRemoteDiagnosticsEnabled,
            InstallId = appSettingsService.Current.Telemetry.InstallId,
            HostUrl = TelemetryDefaults.PostHogEuHost,
            ProjectToken = TelemetryDefaults.ProjectToken,
            RuntimePayloadSource = runtimePayloadSource
        };
    }

    private async Task TrackMediaCreatedAsync(
        FinalMediaTarget target,
        MediaPreflightOptions options,
        bool success,
        string? failedStepName,
        TimeSpan duration,
        string operationId,
        WinPeDiagnostic? diagnostic,
        CancellationToken cancellationToken)
    {
        WinPeRuntimePayloadProvisioningOptions runtimePayloadProvisioning = AddReleaseConnectProvisioning(CreateRuntimePayloadProvisioningOptions(
            options.Architecture,
            Constants.WinPeWorkspaceDirectoryPath,
            Constants.WinPeWorkspaceDirectoryPath,
            Constants.WinPeWorkspaceDirectoryPath));

        string bootMediaTarget = target == FinalMediaTarget.Iso
            ? TelemetryBootMediaTargets.Iso
            : TelemetryBootMediaTargets.Usb;
        string bootMediaUsbOperation = target switch
        {
            FinalMediaTarget.Usb => TelemetryBootMediaUsbOperations.Create,
            FinalMediaTarget.UsbUpdate => TelemetryBootMediaUsbOperations.Update,
            _ => TelemetryBootMediaUsbOperations.None
        };
        IReadOnlyDictionary<string, object?> properties = BootMediaTelemetryPropertyBuilder.Build(
            bootMediaTarget,
            bootMediaUsbOperation,
            options,
            foundryConfigurationStateService.Current,
            success,
            failedStepName,
            duration,
            ResolveRuntimePayloadSource(runtimePayloadProvisioning.Connect),
            ResolveRuntimePayloadSource(runtimePayloadProvisioning.Deploy),
            diagnostic,
            operationId);

        logger.Debug(
            "Tracking media telemetry event. Target={Target}, UsbOperation={UsbOperation}, Success={Success}, FailedStepName={FailedStepName}, DurationSeconds={DurationSeconds}, Architecture={Architecture}, BootImageSource={BootImageSource}, SignatureMode={SignatureMode}, ConnectRuntimePayloadSource={ConnectRuntimePayloadSource}, DeployRuntimePayloadSource={DeployRuntimePayloadSource}.",
            properties["boot_media_target"],
            properties["boot_media_usb_operation"],
            success,
            failedStepName,
            properties["boot_media_creation_duration_seconds"],
            properties["boot_media_architecture"],
            properties["boot_media_boot_image_source"],
            properties["boot_media_signature_mode"],
            properties["boot_media_connect_runtime_payload_source"],
            properties["boot_media_deploy_runtime_payload_source"]);

        await telemetryService.TrackAsync(TelemetryEvents.OsdBootMediaFinished, properties, cancellationToken);
        logger.Debug("Media telemetry event queued. Target={Target}, Success={Success}.", properties["boot_media_target"], success);
    }

    private string BuildStatusText(
        MediaPreflightEvaluation evaluation,
        ConfigurationOverviewEvaluation overview)
    {
        if (evaluation.CanCreateIso && evaluation.CanCreateUsb)
        {
            return localizationService.GetString("StartMedia.Readiness.Status.ReadyIsoAndUsb");
        }

        if (evaluation.CanCreateIso)
        {
            return localizationService.GetString("StartMedia.Readiness.Status.ReadyIso");
        }

        if (evaluation.CanCreateUsb)
        {
            return localizationService.GetString("StartMedia.Readiness.Status.ReadyUsb");
        }

        int actionableItemCount = overview.NeedsAttentionCount;

        return actionableItemCount == 0
            ? localizationService.GetString("StartMedia.Readiness.Status.Warnings")
            : string.Format(localizationService.GetString("StartMedia.Readiness.Status.NeedsAttention"), actionableItemCount);
    }

    private InfoBarSeverity GetReadinessSeverity(MediaPreflightEvaluation evaluation)
    {
        if (evaluation.CanCreateIso && evaluation.CanCreateUsb)
        {
            return InfoBarSeverity.Success;
        }

        if (evaluation.CanCreateIso || evaluation.CanCreateUsb)
        {
            return InfoBarSeverity.Informational;
        }

        if (!GetGlobalBlockingReasons(evaluation).Any(IsBlockingReadinessReason))
        {
            return InfoBarSeverity.Warning;
        }

        return InfoBarSeverity.Error;
    }

    private void RebuildConfigurationOverview(
        MediaPreflightOptions options,
        MediaPreflightEvaluation mediaEvaluation,
        ConfigurationOverviewEvaluation overview)
    {
        FoundryConfigurationDocument configuration = foundryConfigurationStateService.Current;
        NetworkSettings effectiveNetwork = networkSecretStateService.ApplyRequiredSecrets(configuration.Network);

        IsGeneralConfigurationOverviewExpanded = ReplaceOverviewItems(
            GeneralConfigurationOverviewItems,
            BuildGeneralConfigurationOverview(configuration, options, mediaEvaluation, overview));
        IsNetworkOverviewExpanded = ReplaceOverviewItems(
            NetworkOverviewItems,
            BuildNetworkOverview(configuration, effectiveNetwork, overview));
        IsAutopilotOverviewExpanded = ReplaceOverviewItems(
            AutopilotOverviewItems,
            BuildAutopilotOverview(options, overview));
        IsCustomizationOverviewExpanded = ReplaceOverviewItems(
            CustomizationOverviewItems,
            BuildCustomizationOverview(configuration, overview));

        GeneralConfigurationOverviewSummary = GetOverviewSummary(GeneralConfigurationOverviewItems);
        NetworkOverviewSummary = GetOverviewSummary(NetworkOverviewItems);
        AutopilotOverviewSummary = GetOverviewSummary(AutopilotOverviewItems);
        CustomizationOverviewSummary = GetOverviewSummary(CustomizationOverviewItems);
    }

    private IReadOnlyList<StartConfigurationOverviewItemViewModel> BuildGeneralConfigurationOverview(
        FoundryConfigurationDocument configuration,
        MediaPreflightOptions options,
        MediaPreflightEvaluation mediaEvaluation,
        ConfigurationOverviewEvaluation overview)
    {
        GeneralSettings general = configuration.General;
        bool oobePasswordRequiresDeploymentProtection =
            OobeAccountConfigurationValidator.RequiresProtectedMedia(configuration.Customization.Oobe) &&
            !general.DeploymentProtection.IsEnabled;
        MediaPreflightBlockingReason? languageReason = GetFirstReason(
            mediaEvaluation,
            MediaPreflightBlockingReason.MissingWinPeLanguage,
            MediaPreflightBlockingReason.WinPeLanguageUnavailable);
        MediaPreflightBlockingReason? driverReason = GetFirstReason(
            mediaEvaluation,
            MediaPreflightBlockingReason.CustomDriverDirectoryNotFound,
            MediaPreflightBlockingReason.CustomDriverDirectoryHasNoInfFiles);

        return
        (StartConfigurationOverviewItemViewModel[])
        [
            CreateOverviewItem(ConfigurationOverviewItem.Architecture, overview, "StartMedia.Field.Architecture",
                FormatArchitecture(options.Architecture), ConfigurationNavigationTarget.General),
            CreateOverviewItem(ConfigurationOverviewItem.SecureBoot, overview, "GeneralConfiguration.SecureBoot.Header",
                FormatSignatureMode(options.SignatureMode), ConfigurationNavigationTarget.General),
            CreateOverviewItem(ConfigurationOverviewItem.WinPeLanguage, overview, "StartMedia.Field.WinPeLanguage",
                languageReason.HasValue ? GetBlockingReasonText(languageReason.Value) : FormatValue(WinPeLanguage),
                ConfigurationNavigationTarget.General),
            CreateOverviewItem(ConfigurationOverviewItem.TimeZone, overview, "GeneralConfiguration.TimeZone.Header",
                string.IsNullOrWhiteSpace(configuration.Localization.DefaultTimeZoneId)
                    ? localizationService.GetString("Common.AutomaticOption")
                    : configuration.Localization.DefaultTimeZoneId,
                ConfigurationNavigationTarget.General),
            CreateOverviewItem(ConfigurationOverviewItem.DeploymentCompletion, overview, "GeneralConfiguration.Completion.Header",
                general.AutomaticRebootEnabled
                    ? $"{DeploymentRebootDelay.NormalizeRuntime(general.AutomaticRebootDelaySeconds)} s"
                    : localizationService.GetString("Common.Disabled"),
                ConfigurationNavigationTarget.General),
            CreateOverviewItem(ConfigurationOverviewItem.DeploymentProtection, overview, "GeneralConfiguration.DeploymentProtection.Header",
                localizationService.GetString(oobePasswordRequiresDeploymentProtection
                    ? "Customization.OobeAccountPasswordDescription"
                    : "GeneralConfiguration.DeploymentProtection.Description"),
                ConfigurationNavigationTarget.General),
            CreateOverviewItem(ConfigurationOverviewItem.DriverOptions, overview, "StartMedia.DriverOptions.Header",
                driverReason.HasValue
                    ? GetBlockingReasonText(driverReason.Value)
                    : FormatDriverOptions(options.DriverVendors, options.CustomDriverDirectoryPath),
                ConfigurationNavigationTarget.General)
        ];
    }

    private IReadOnlyList<StartConfigurationOverviewItemViewModel> BuildNetworkOverview(
        FoundryConfigurationDocument configuration,
        NetworkSettings effectiveNetwork,
        ConfigurationOverviewEvaluation overview)
    {
        string ethernetDescription = configuration.Network.Dot1x.IsEnabled
            ? FormatValue(Path.GetFileName(configuration.Network.Dot1x.ProfileTemplatePath))
            : localizationService.GetString("Nav_EthernetDot1xKey.Description");
        string wifiDescription = configuration.Network.Wifi.IsEnabled
            ? string.Join(" · ", new[]
            {
                configuration.Network.Wifi.Ssid,
                configuration.Network.Wifi.SecurityType
            }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : localizationService.GetString("Nav_WifiKey.Description");
        NetworkConfigurationValidationResult ethernetValidation = NetworkConfigurationValidator.Validate(effectiveNetwork with
        {
            WifiProvisioned = false,
            Wifi = new WifiSettings()
        });
        NetworkConfigurationValidationResult wifiValidation = NetworkConfigurationValidator.Validate(effectiveNetwork with
        {
            Dot1x = new Dot1xSettings()
        });

        return
        (StartConfigurationOverviewItemViewModel[])
        [
            CreateOverviewItem(ConfigurationOverviewItem.EthernetDot1x, overview, "Nav_EthernetDot1xKey.Title",
                GetNetworkDescription(overview[ConfigurationOverviewItem.EthernetDot1x], ethernetDescription, ethernetValidation),
                ConfigurationNavigationTarget.EthernetDot1x),
            CreateOverviewItem(ConfigurationOverviewItem.Wifi, overview, "Nav_WifiKey.Title",
                GetNetworkDescription(overview[ConfigurationOverviewItem.Wifi], FormatValue(wifiDescription), wifiValidation),
                ConfigurationNavigationTarget.Wifi)
        ];
    }

    private IReadOnlyList<StartConfigurationOverviewItemViewModel> BuildAutopilotOverview(
        MediaPreflightOptions options,
        ConfigurationOverviewEvaluation overview)
    {
        string activeDescription = options.IsAutopilotConfigurationReady
            ? FormatAutopilot(options)
            : GetAutopilotValidationText(options.AutopilotConfigurationValidationCode);

        return
        (StartConfigurationOverviewItemViewModel[])
        [
            CreateOverviewItem(ConfigurationOverviewItem.AutopilotJsonProfile, overview, "Nav_AutopilotJsonProfileKey.Title",
                overview[ConfigurationOverviewItem.AutopilotJsonProfile] == ConfigurationOverviewState.NotSelected
                    ? localizationService.GetString("Nav_AutopilotJsonProfileKey.Description")
                    : activeDescription,
                ConfigurationNavigationTarget.AutopilotJsonProfile),
            CreateOverviewItem(ConfigurationOverviewItem.AutopilotZeroTouch, overview, "Nav_AutopilotZeroTouchKey.Title",
                overview[ConfigurationOverviewItem.AutopilotZeroTouch] == ConfigurationOverviewState.NotSelected
                    ? localizationService.GetString("Nav_AutopilotZeroTouchKey.Description")
                    : activeDescription,
                ConfigurationNavigationTarget.AutopilotHardwareHashUpload),
            CreateOverviewItem(ConfigurationOverviewItem.AutopilotInteractive, overview, "Nav_AutopilotInteractiveHashUploadKey.Title",
                overview[ConfigurationOverviewItem.AutopilotInteractive] == ConfigurationOverviewState.NotSelected
                    ? localizationService.GetString("Nav_AutopilotInteractiveHashUploadKey.Description")
                    : activeDescription,
                ConfigurationNavigationTarget.AutopilotInteractiveHardwareHashUpload)
        ];
    }

    private IReadOnlyList<StartConfigurationOverviewItemViewModel> BuildCustomizationOverview(
        FoundryConfigurationDocument configuration,
        ConfigurationOverviewEvaluation overview)
    {
        CustomizationSettings customization = configuration.Customization;
        bool oobePasswordRequiresDeploymentProtection =
            OobeAccountConfigurationValidator.RequiresProtectedMedia(customization.Oobe) &&
            !configuration.General.DeploymentProtection.IsEnabled;
        OperatingSystemSelectionSettings os = configuration.OperatingSystemSelection;
        string osDescription = string.Join(" · ", new[]
        {
            os.DefaultLanguageCode,
            os.DefaultReleaseId,
            os.DefaultEdition
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        WindowsOptionalFeatureSettings optionalFeatures = customization.WindowsOptionalFeatures;
        int configuredFeatureCount = optionalFeatures.EnabledFeatureIds.Count + optionalFeatures.DisabledFeatureIds.Count;
        string optionalFeatureDescription = string.Format(
            localizationService.GetString("Customization.WindowsOptionalFeatures.SummaryFormat"),
            configuredFeatureCount,
            optionalFeatures.EnabledFeatureIds.Count,
            optionalFeatures.DisabledFeatureIds.Count);

        return
        (StartConfigurationOverviewItemViewModel[])
        [
            CreateOverviewItem(ConfigurationOverviewItem.OperatingSystemSelection, overview, "Nav_OsSelectionKey.Title",
                string.IsNullOrWhiteSpace(osDescription) ? localizationService.GetString("Nav_OsSelectionKey.Description") : osDescription,
                ConfigurationNavigationTarget.OperatingSystemSelection),
            CreateOverviewItem(ConfigurationOverviewItem.MachineNaming, overview, "Nav_MachineNamingKey.Title",
                customization.MachineNaming.IsEnabled
                    ? localizationService.GetString(customization.MachineNaming.Mode == MachineNamingMode.Composed
                        ? "Customization.MachineNamingComposedLabel"
                        : "Customization.MachineNamingManualLabel")
                    : localizationService.GetString("Nav_MachineNamingKey.Description"),
                ConfigurationNavigationTarget.MachineNaming),
            CreateOverviewItem(ConfigurationOverviewItem.Oobe, overview, "Nav_OobeKey.Title",
                oobePasswordRequiresDeploymentProtection
                    ? localizationService.GetString("Customization.OobeAccountPasswordDescription")
                    : customization.Oobe.IsEnabled
                    ? FormatOobeSummary(customization.Oobe)
                    : localizationService.GetString("Nav_OobeKey.Description"),
                ConfigurationNavigationTarget.Oobe),
            CreateOverviewItem(ConfigurationOverviewItem.Unattend, overview, "Nav_UnattendKey.Title",
                localizationService.GetString("Nav_UnattendKey.Description"),
                ConfigurationNavigationTarget.Unattend),
            CreateOverviewItem(ConfigurationOverviewItem.OptionalFeatures, overview, "Nav_OptionalFeaturesKey.Title",
                optionalFeatures.IsEnabled
                    ? optionalFeatureDescription
                    : localizationService.GetString("Nav_OptionalFeaturesKey.Description"),
                ConfigurationNavigationTarget.WindowsOptionalFeatures),
            CreateOverviewItem(ConfigurationOverviewItem.AppxRemoval, overview, "Nav_AppRemovalKey.Title",
                customization.AppxRemoval.IsEnabled
                    ? $"{localizationService.GetString("Customization.AppxRemovalPackagesLabel")}: {customization.AppxRemoval.PackageNames.Count}"
                    : localizationService.GetString("Nav_AppRemovalKey.Description"),
                ConfigurationNavigationTarget.AppxRemoval),
            CreateOverviewItem(ConfigurationOverviewItem.AiComponents, overview, "Nav_AiComponentsKey.Title",
                localizationService.GetString("Nav_AiComponentsKey.Description"),
                ConfigurationNavigationTarget.AiComponentRemoval)
        ];
    }

    private StartConfigurationOverviewItemViewModel CreateOverviewItem(
        ConfigurationOverviewItem item,
        ConfigurationOverviewEvaluation evaluation,
        string titleKey,
        string description,
        ConfigurationNavigationTarget errorNavigationTarget)
    {
        ConfigurationOverviewState state = evaluation[item];
        string title = localizationService.GetString(titleKey);
        bool needsAttention = state == ConfigurationOverviewState.NeedsAttention;
        string actionText = needsAttention
            ? $"{localizationService.GetString("StartMedia.Readiness.Action.Review")} {title}"
            : string.Empty;

        return new StartConfigurationOverviewItemViewModel(
            title,
            description,
            GetOverviewStatus(state),
            GetOverviewGlyph(state),
            GetOverviewGlyphBrushKey(state),
            state,
            needsAttention ? errorNavigationTarget : ConfigurationNavigationTarget.None,
            actionText,
            actionText);
    }

    private string GetNetworkDescription(
        ConfigurationOverviewState state,
        string configuredDescription,
        NetworkConfigurationValidationResult validation) =>
        state == ConfigurationOverviewState.NeedsAttention
            ? NetworkConfigurationValidationTextFormatter.Format(localizationService, validation)
            : configuredDescription;

    private string FormatOobeSummary(OobeSettings settings)
    {
        string diagnosticData = localizationService.GetString($"Customization.OobeDiagnosticData{settings.DiagnosticDataLevel}");
        string locationAccess = localizationService.GetString(settings.LocationAccess == OobeLocationAccessMode.ForceOff
            ? "Customization.OobeLocationForceOff"
            : "Customization.OobeLocationUserControlled");
        List<string> parts =
        [
            diagnosticData,
            locationAccess
        ];

        if (settings.EnableAdministratorAccount)
        {
            parts.Add(localizationService.GetString("StartMedia.OobeSummaryAdministratorEnabled"));
        }

        if (settings.AdditionalAccounts.Count > 0)
        {
            parts.Add(localizationService.FormatString("StartMedia.OobeSummaryAdditionalAccountsFormat", settings.AdditionalAccounts.Count));
            parts.Add(localizationService.GetString("StartMedia.OobeSummarySkipUserAccountCreation"));
        }

        return string.Join(" · ", parts);
    }

    private string GetOverviewStatus(ConfigurationOverviewState state) => state switch
    {
        ConfigurationOverviewState.Configured => localizationService.GetString("NavigationStatus.Configured"),
        ConfigurationOverviewState.Disabled => localizationService.GetString("Common.Disabled"),
        ConfigurationOverviewState.NotConfigured => localizationService.GetString("NavigationStatus.NotConfigured"),
        ConfigurationOverviewState.Default => localizationService.GetString("StartOverview.State.Default"),
        ConfigurationOverviewState.NotSelected => localizationService.GetString("StartOverview.State.NotSelected"),
        _ => localizationService.GetString("Common.NeedsAttention")
    };

    private string GetOverviewSummary(IEnumerable<StartConfigurationOverviewItemViewModel> items)
    {
        List<StartConfigurationOverviewItemViewModel> attentionItems = items
            .Where(item => item.NavigationTarget != ConfigurationNavigationTarget.None)
            .ToList();
        if (attentionItems.Count == 0)
        {
            IReadOnlyList<ConfigurationOverviewState> states = items.Select(item => item.State).ToList();
            if (states.Contains(ConfigurationOverviewState.Configured))
            {
                return localizationService.GetString("NavigationStatus.Configured");
            }

            if (states.Contains(ConfigurationOverviewState.Default))
            {
                return localizationService.GetString("StartOverview.State.Default");
            }

            if (states.All(state => state == ConfigurationOverviewState.NotSelected))
            {
                return localizationService.GetString("StartOverview.State.NotSelected");
            }

            return states.All(state => state == ConfigurationOverviewState.NotConfigured)
                ? localizationService.GetString("NavigationStatus.NotConfigured")
                : localizationService.GetString("Common.Disabled");
        }

        string additionalItemCount = attentionItems.Count > 1 ? $" (+{attentionItems.Count - 1})" : string.Empty;
        return $"{localizationService.GetString("Common.NeedsAttention")}: {attentionItems[0].Title}{additionalItemCount}";
    }

    private static bool ReplaceOverviewItems(
        ObservableCollection<StartConfigurationOverviewItemViewModel> target,
        IEnumerable<StartConfigurationOverviewItemViewModel> items)
    {
        target.Clear();
        bool hasAttentionItem = false;
        foreach (StartConfigurationOverviewItemViewModel item in items)
        {
            target.Add(item);
            hasAttentionItem |= item.NavigationTarget != ConfigurationNavigationTarget.None;
        }

        return hasAttentionItem;
    }

    private static MediaPreflightBlockingReason? GetFirstReason(
        MediaPreflightEvaluation evaluation,
        params MediaPreflightBlockingReason[] reasons)
    {
        foreach (MediaPreflightBlockingReason reason in reasons)
        {
            if (HasReason(evaluation, reason))
            {
                return reason;
            }
        }

        return null;
    }

    private static bool HasReason(MediaPreflightEvaluation evaluation, MediaPreflightBlockingReason reason)
    {
        return evaluation.IsoBlockingReasons.Contains(reason) || evaluation.UsbBlockingReasons.Contains(reason);
    }

    private static string GetOverviewGlyph(ConfigurationOverviewState state)
    {
        return state switch
        {
            ConfigurationOverviewState.Configured or ConfigurationOverviewState.Default => "\uE8FB",
            ConfigurationOverviewState.NeedsAttention => "\uE711",
            _ => "\uE946"
        };
    }

    private static string GetOverviewGlyphBrushKey(ConfigurationOverviewState state)
    {
        return state switch
        {
            ConfigurationOverviewState.Configured or ConfigurationOverviewState.Default => "FoundryStatusReadyBrush",
            ConfigurationOverviewState.NeedsAttention => "FoundryStatusBlockedBrush",
            _ => "FoundryStatusNeutralBrush"
        };
    }

    private string BuildGlobalSummary(MediaPreflightOptions options, MediaPreflightEvaluation evaluation)
    {
        IReadOnlyList<MediaPreflightBlockingReason> reasons = GetGlobalBlockingReasons(evaluation);
        var builder = new StringBuilder();
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Adk")}: {FormatReady(adkService.CurrentStatus.CanCreateMedia)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.WinPeLanguage")}: {FormatValue(NormalizeCultureName(options.WinPeLanguage))}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.BootImageSource")}: {FormatBootImageSource(options.BootImageSource)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Architecture")}: {FormatArchitecture(options.Architecture)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.IsoPath")}: {FormatValue(options.IsoOutputPath)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.UsbTarget")}: {FormatUsbCandidate(options.SelectedUsbDisk)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Signature")}: {FormatSignatureMode(options.SignatureMode)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.PartitionStyle")}: {evaluation.EffectiveUsbPartitionStyle}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.FormatMode")}: {FormatUsbFormatMode(options.UsbFormatMode)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Drivers")}: {FormatDriverOptions(options.DriverVendors, options.CustomDriverDirectoryPath)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Network")}: {FormatReady(options.IsNetworkConfigurationReady)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Deploy")}: {FormatReady(options.IsDeployConfigurationReady)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Connect")}: {FormatReady(options.IsConnectProvisioningReady)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Secrets")}: {FormatReady(options.AreRequiredSecretsReady)}");
        builder.AppendLine($"{localizationService.GetString("StartMedia.Field.Autopilot")}: {FormatAutopilot(options)}");
        builder.AppendLine();
        builder.AppendLine(options.IsFinalExecutionEnabled
            ? localizationService.GetString("StartMedia.FinalExecution.Ready")
            : localizationService.GetString("StartMedia.FinalExecution.Deferred"));

        AppendBlockingReasons(builder, reasons);

        return builder.ToString();
    }

    private void AppendBlockingReasons(StringBuilder builder, IReadOnlyList<MediaPreflightBlockingReason> reasons)
    {
        if (reasons.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine(localizationService.GetString("StartMedia.Summary.BlockingReasons"));
        foreach (MediaPreflightBlockingReason reason in reasons)
        {
            builder.AppendLine($"- {GetBlockingReasonText(reason)}");
        }
    }

    private string GetBlockingReasonText(MediaPreflightBlockingReason reason)
    {
        return localizationService.GetString($"StartMedia.BlockingReason.{reason}");
    }

    private void LogPreflightSummary(MediaPreflightOptions options, MediaPreflightEvaluation evaluation)
    {
        IReadOnlyList<MediaPreflightBlockingReason> reasons = GetGlobalBlockingReasons(evaluation);

        logger.Information(
            "Media preflight summary refreshed. Architecture={Architecture}, WinPeLanguage={WinPeLanguage}, BootImageSource={BootImageSource}, IsoOutputPath={IsoOutputPath}, UsbTargetSelected={UsbTargetSelected}, DiskNumber={DiskNumber}, DiskName={DiskName}, NetworkReady={NetworkReady}, DeployReady={DeployReady}, ConnectReady={ConnectReady}, SecretsReady={SecretsReady}, AutopilotEnabled={AutopilotEnabled}, AutopilotReady={AutopilotReady}, HasAutopilotProfile={HasAutopilotProfile}, HasAutopilotFolder={HasAutopilotFolder}, SummaryReady={SummaryReady}, BlockingReasons={BlockingReasons}",
            options.Architecture,
            NormalizeCultureName(options.WinPeLanguage),
            options.BootImageSource,
            options.IsoOutputPath,
            options.SelectedUsbDisk is not null,
            options.SelectedUsbDisk?.DiskNumber,
            options.SelectedUsbDisk?.FriendlyName,
            options.IsNetworkConfigurationReady,
            options.IsDeployConfigurationReady,
            options.IsConnectProvisioningReady,
            options.AreRequiredSecretsReady,
            options.IsAutopilotEnabled,
            options.IsAutopilotConfigurationReady,
            !string.IsNullOrWhiteSpace(options.AutopilotProfileDisplayName),
            !string.IsNullOrWhiteSpace(options.AutopilotProfileFolderName),
            evaluation.CanGenerateIsoSummary || evaluation.CanGenerateUsbSummary,
            string.Join(",", reasons));
    }

    private static IReadOnlyList<MediaPreflightBlockingReason> GetGlobalBlockingReasons(MediaPreflightEvaluation evaluation)
    {
        return evaluation.IsoBlockingReasons
            .Concat(evaluation.UsbBlockingReasons)
            .Where(reason => reason != MediaPreflightBlockingReason.NoUsbTarget)
            .Distinct()
            .ToList();
    }

    private static bool IsBlockingReadinessReason(MediaPreflightBlockingReason reason)
    {
        return reason is not (MediaPreflightBlockingReason.InvalidIsoPath
            or MediaPreflightBlockingReason.NoUsbTarget
            or MediaPreflightBlockingReason.Arm64RequiresGpt);
    }

    private IReadOnlyList<string> GetAvailableWinPeLanguages()
    {
        if (!adkService.CurrentStatus.CanCreateMedia)
        {
            return [];
        }

        WinPeResult<WinPeToolPaths> toolsResult = new WinPeToolResolver().ResolveTools(adkService.CurrentStatus.KitsRootPath);
        if (!toolsResult.IsSuccess || toolsResult.Value is null)
        {
            logger.Warning("WinPE language validation skipped because ADK tools were not resolved. ErrorCode={ErrorCode}", toolsResult.Error?.Code);
            return [];
        }

        WinPeResult<IReadOnlyList<string>> languagesResult = languageDiscoveryService.GetAvailableLanguages(
            new WinPeLanguageDiscoveryOptions
            {
                Tools = toolsResult.Value,
                Architecture = SelectedArchitecture?.Value ?? WinPeArchitecture.X64
            });

        if (!languagesResult.IsSuccess || languagesResult.Value is null)
        {
            logger.Warning("WinPE language validation skipped because language discovery failed. ErrorCode={ErrorCode}", languagesResult.Error?.Code);
            return [];
        }

        return languagesResult.Value.Select(NormalizeCultureName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RebuildFormatModes()
    {
        UsbFormatMode selectedValue = SelectedFormatMode?.Value
            ?? foundryConfigurationStateService.Current.General.UsbFormatMode;

        FormatModes.Clear();
        FormatModes.Add(new(UsbFormatMode.Quick, localizationService.GetString("StartMedia.FormatMode.Quick")));
        FormatModes.Add(new(UsbFormatMode.Complete, localizationService.GetString("StartMedia.FormatMode.Complete")));
        SelectedFormatMode = SelectOption(FormatModes, selectedValue);
    }

    private void RebuildPartitionStyles()
    {
        UsbPartitionStyle selectedValue = SelectedArchitecture?.Value == WinPeArchitecture.Arm64
            ? UsbPartitionStyle.Gpt
            : SelectedPartitionStyle?.Value ?? foundryConfigurationStateService.Current.General.UsbPartitionStyle;

        PartitionStyles.Clear();
        PartitionStyles.Add(new(UsbPartitionStyle.Gpt, "GPT"));

        if (SelectedArchitecture?.Value != WinPeArchitecture.Arm64)
        {
            PartitionStyles.Add(new(UsbPartitionStyle.Mbr, "MBR"));
        }

        SelectedPartitionStyle = SelectOption(PartitionStyles, selectedValue) ?? PartitionStyles[0];
    }

    private void RebuildUsbCandidateDisplayNames()
    {
        List<WinPeUsbDiskCandidate> candidates = UsbCandidates.Select(option => option.Value).ToList();
        WinPeUsbDiskCandidate? selectedCandidate = SelectedUsbDisk?.Value;

        UsbCandidates.Clear();
        foreach (WinPeUsbDiskCandidate candidate in candidates)
        {
            UsbCandidates.Add(CreateUsbDiskOption(candidate));
        }

        SelectedUsbDisk = UsbCandidates.FirstOrDefault(option => option.Value.DiskNumber == selectedCandidate?.DiskNumber)
            ?? UsbCandidates.FirstOrDefault();
    }

    private SelectionOption<WinPeUsbDiskCandidate> CreateUsbDiskOption(WinPeUsbDiskCandidate candidate)
    {
        return new SelectionOption<WinPeUsbDiskCandidate>(
            candidate,
            string.Format(
                localizationService.GetString("StartMedia.Usb.DiskDisplay"),
                candidate.DiskNumber,
                candidate.FriendlyName,
                FormatByteSize(candidate.SizeBytes)));
    }

    private string FormatReady(bool isReady)
    {
        return isReady ? localizationService.GetString("Common.Enabled") : localizationService.GetString("Common.Disabled");
    }

    private string FormatAutopilot(MediaPreflightOptions options)
    {
        if (!options.IsAutopilotEnabled)
        {
            return FormatReady(false);
        }

        if (!options.IsAutopilotConfigurationReady)
        {
            return GetAutopilotValidationText(options.AutopilotConfigurationValidationCode);
        }

        if (options.AutopilotProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload)
        {
            return localizationService.GetString("StartMedia.Autopilot.HardwareHashUpload");
        }

        if (options.AutopilotProvisioningMode == AutopilotProvisioningMode.InteractiveHardwareHashUpload)
        {
            return localizationService.GetString("StartMedia.Autopilot.InteractiveHardwareHashUpload");
        }

        return string.Format(
            localizationService.GetString("StartMedia.Autopilot.ProfileFormat"),
            FormatValue(options.AutopilotProfileDisplayName),
            FormatValue(options.AutopilotProfileFolderName));
    }

    private string GetAutopilotValidationText(AutopilotConfigurationValidationCode code)
    {
        string key = $"StartMedia.Autopilot.Validation.{code}";
        string value = localizationService.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal)
            ? GetBlockingReasonText(MediaPreflightBlockingReason.AutopilotConfigurationNotReady)
            : value;
    }

    private static string FormatValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string NormalizeCultureName(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        try
        {
            return CultureInfo.GetCultureInfo(language).Name;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    private string FormatDriverOptions(IReadOnlyList<WinPeVendorSelection> vendors, string? customDriverDirectoryPath)
    {
        List<string> parts = vendors.Select(FormatDriverVendor).ToList();
        if (!string.IsNullOrWhiteSpace(customDriverDirectoryPath))
        {
            parts.Add(customDriverDirectoryPath);
        }

        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    private string FormatUsbCandidate(WinPeUsbDiskCandidate? candidate)
    {
        if (candidate is null)
        {
            return "-";
        }

        return string.Format(
            localizationService.GetString("StartMedia.Usb.DiskDisplay"),
            candidate.DiskNumber,
            candidate.FriendlyName,
            FormatByteSize(candidate.SizeBytes));
    }

    private string FormatArchitecture(WinPeArchitecture architecture)
    {
        return architecture switch
        {
            WinPeArchitecture.X64 => "x64",
            WinPeArchitecture.Arm64 => "arm64",
            _ => architecture.ToString()
        };
    }

    private string FormatSignatureMode(WinPeSignatureMode signatureMode)
    {
        return localizationService.GetString($"StartMedia.SignatureMode.{signatureMode}");
    }

    private string FormatBootImageSource(WinPeBootImageSource bootImageSource)
    {
        return localizationService.GetString($"StartMedia.BootImageSource.{bootImageSource}");
    }

    private string FormatUsbFormatMode(UsbFormatMode formatMode)
    {
        return localizationService.GetString($"StartMedia.FormatMode.{formatMode}");
    }

    private string FormatDriverVendor(WinPeVendorSelection vendor)
    {
        return localizationService.GetString($"StartMedia.DriverVendor.{vendor}");
    }

    private string FormatByteSize(ulong bytes)
    {
        double size = bytes;
        string[] units =
        [
            localizationService.GetString("StartMedia.ByteUnit.B"),
            localizationService.GetString("StartMedia.ByteUnit.KB"),
            localizationService.GetString("StartMedia.ByteUnit.MB"),
            localizationService.GetString("StartMedia.ByteUnit.GB"),
            localizationService.GetString("StartMedia.ByteUnit.TB")
        ];
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }

    private WinPeToolPaths ResolveWinPeToolsOrThrow()
    {
        WinPeResult<WinPeToolPaths> result = new WinPeToolResolver().ResolveTools(adkService.CurrentStatus.KitsRootPath);
        EnsureSuccess(result);
        return result.Value!;
    }

    private WinPeBootImageSource ResolveBootImageSource()
    {
        NetworkSettings network = foundryConfigurationStateService.Current.Network;
        return network.WifiProvisioned || network.Wifi.IsEnabled
            ? WinPeBootImageSource.WinReWifi
            : WinPeBootImageSource.WinPe;
    }

    private WinPeRuntimePayloadProvisioningOptions CreateRuntimePayloadProvisioningOptions(
        WinPeArchitecture architecture,
        string workingDirectoryPath,
        string mountedImagePath,
        string usbCacheRootPath)
    {
        return WinPeRuntimePayloadProvisioningOptions.CreateDeveloperOptions(
            architecture,
            workingDirectoryPath,
            mountedImagePath,
            usbCacheRootPath,
            Debugger.IsAttached,
            projectDiscoveryStartPath: FindRepositoryRoot());
    }

    private static WinPeRuntimePayloadProvisioningOptions AddReleaseConnectProvisioning(
        WinPeRuntimePayloadProvisioningOptions options)
    {
        if (options.Connect.IsEnabled)
        {
            return options;
        }

        return options with
        {
            Connect = new WinPeRuntimePayloadApplicationOptions
            {
                IsEnabled = true,
                ProvisioningSource = WinPeProvisioningSource.Release
            }
        };
    }

    private static string ResolveCurlExecutablePath()
    {
        string systemCurlPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "curl.exe");

        return File.Exists(systemCurlPath) ? systemCurlPath : "curl.exe";
    }

    private static string? FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "Foundry.Connect", "Foundry.Connect.csproj")) &&
                File.Exists(Path.Combine(current.FullName, "src", "Foundry.Deploy", "Foundry.Deploy.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static void EnsureSuccess(WinPeResult result)
    {
        if (result.IsSuccess)
        {
            return;
        }

        throw new WinPeOperationException(result.Error);
    }

    private sealed class WinPeOperationException(WinPeDiagnostic? diagnostic)
        : InvalidOperationException(FormatWinPeError(diagnostic), diagnostic?.Exception)
    {
        public WinPeDiagnostic? Diagnostic { get; } = diagnostic;
    }

    private static string FormatWinPeError(WinPeDiagnostic? diagnostic)
    {
        if (diagnostic is null)
        {
            return "WinPE operation failed.";
        }

        return string.IsNullOrWhiteSpace(diagnostic.Details)
            ? diagnostic.Message
            : $"{diagnostic.Message}{Environment.NewLine}{diagnostic.Details}";
    }

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: true, out T result) ? result : fallback;
    }

    private static SelectionOption<T>? SelectOption<T>(IEnumerable<SelectionOption<T>> options, T value)
    {
        return options.FirstOrDefault(option => EqualityComparer<T>.Default.Equals(option.Value, value));
    }

    private enum FinalMediaTarget
    {
        Iso,
        Usb,
        UsbUpdate
    }

    private enum UsbCandidateDiscoveryState
    {
        NotLoaded,
        Loading,
        Ready,
        Empty,
        Error,
        Blocked
    }

    private sealed record PreparedMediaWorkspace(
        WinPeWorkspacePreparationResult PreparedWorkspace,
        WinPeToolPaths Tools,
        WinPeRuntimePayloadProvisioningOptions RuntimePayloadProvisioning);
}
