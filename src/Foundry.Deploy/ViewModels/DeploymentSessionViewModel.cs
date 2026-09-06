// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Models.Configuration;
using Foundry.Utilities.Networking;
using Foundry.Utilities.Diagnostics;
using Foundry.Utilities.Runtime;
using Foundry.Utilities.Storage;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Localization;
using Foundry.Utilities.Processes;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Operations;
using Foundry.Deploy.Services.System;
using Foundry.Deploy.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Foundry.Deploy.ViewModels;

public sealed partial class DeploymentSessionViewModel : LocalizedViewModelBase
{
    private const int ShellFolderPickerUnavailableHResult = unchecked((int)0x80040111);
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly IOperationProgressService _operationProgressService;
    private readonly IDeploymentOrchestrator _deploymentOrchestrator;
    private readonly IProcessRunner _processRunner;
    private readonly INetworkAdapterSnapshotProvider _networkAdapterSnapshotProvider;
    private readonly bool _isDebugSafeMode;
    private readonly DeploymentTimelineTracker _timelineTracker;
    private string _rawCurrentStepName = "Waiting for deployment...";
    private string _rawCurrentStepProgressText = "Waiting for progress...";
    private string _rawFailedStepName = string.Empty;
    private string _rawFailedStepErrorMessage = string.Empty;
    private DispatcherTimer? _elapsedTimeTimer;
    private DispatcherTimer? _rebootCountdownTimer;
    private DateTimeOffset? _deploymentStartTimeUtc;
    private int _activeStepIndex;
    private int _plannedStepCount;
    private string _lastLogsDirectoryPath = string.Empty;
    private bool _isDeploymentInProgress;
    private bool _isRebootInProgress;
    private bool _isDisposed;
    private DeploymentRebootPolicy _rebootPolicy = DeploymentRebootPolicy.Create(settings: null);

    public DeploymentSessionViewModel(
        Dispatcher dispatcher,
        ILogger logger,
        IOperationProgressService operationProgressService,
        IDeploymentOrchestrator deploymentOrchestrator,
        IProcessRunner processRunner,
        ILocalizationService localizationService,
        bool isDebugSafeMode,
        INetworkAdapterSnapshotProvider? networkAdapterSnapshotProvider = null)
        : base(localizationService)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _operationProgressService = operationProgressService ?? throw new ArgumentNullException(nameof(operationProgressService));
        _deploymentOrchestrator = deploymentOrchestrator ?? throw new ArgumentNullException(nameof(deploymentOrchestrator));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _networkAdapterSnapshotProvider = networkAdapterSnapshotProvider ?? new WindowsNetworkAdapterSnapshotProvider();
        _isDebugSafeMode = isDebugSafeMode;
        _timelineTracker = new DeploymentTimelineTracker(
            DeploymentUiTextLocalizer.LocalizeStepName,
            LocalizeTimelineState);

        _operationProgressService.ProgressChanged += OnOperationProgressChanged;
        _deploymentOrchestrator.StepProgressChanged += OnStepProgressChanged;
        LocalizationService.LanguageChanged += OnLocalizationLanguageChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupReady))]
    private bool isStartupInitializing = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplashPage))]
    [NotifyPropertyChangedFor(nameof(IsSuccessPage))]
    [NotifyPropertyChangedFor(nameof(IsProgressPage))]
    [NotifyPropertyChangedFor(nameof(IsErrorPage))]
    [NotifyPropertyChangedFor(nameof(IsDeploymentStatusPage))]
    private DeploymentPage currentPage = DeploymentPage.Splash;

    [ObservableProperty]
    private int deploymentProgress;

    [ObservableProperty]
    private bool isGlobalProgressIndeterminate = true;

    [ObservableProperty]
    private string currentStepName = LocalizationText.GetString("Status.WaitingForDeployment");

    [ObservableProperty]
    private string stepCounterText = LocalizationText.GetString("Status.StepCounterUnknown");

    [ObservableProperty]
    private double currentStepProgress;

    [ObservableProperty]
    private bool isCurrentStepProgressIndeterminate = true;

    [ObservableProperty]
    private string currentStepProgressText = LocalizationText.GetString("Status.WaitingForProgress");

    [ObservableProperty]
    private string computerNameText = string.Empty;

    [ObservableProperty]
    private string ipAddress = LocalizationText.GetString("Common.NotAvailable");

    [ObservableProperty]
    private string subnetMask = LocalizationText.GetString("Common.NotAvailable");

    [ObservableProperty]
    private string gatewayAddress = LocalizationText.GetString("Common.NotAvailable");

    [ObservableProperty]
    private string macAddress = LocalizationText.GetString("Common.NotAvailable");

    [ObservableProperty]
    private string startTimeText = LocalizationText.GetString("Common.NotAvailable");

    [ObservableProperty]
    private string elapsedTimeText = "00:00:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebootNowCommand))]
    [NotifyPropertyChangedFor(nameof(CompletionInstructionText))]
    private int rebootCountdownSeconds = DeploymentRebootDelay.DefaultSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailedStepText))]
    private string failedStepName = string.Empty;

    [ObservableProperty]
    private string failedStepErrorMessage = string.Empty;

    public bool IsSplashPage => CurrentPage == DeploymentPage.Splash;

    public bool IsSuccessPage => CurrentPage == DeploymentPage.Success;
    public bool IsProgressPage => CurrentPage == DeploymentPage.Progress;
    public bool IsErrorPage => CurrentPage == DeploymentPage.Error;
    public bool IsDeploymentStatusPage => CurrentPage is DeploymentPage.Progress or DeploymentPage.Success or DeploymentPage.Error;

    public bool IsStartupReady => !IsStartupInitializing;
    public ObservableCollection<DeploymentTimelineEntryViewModel> TimelineEntries => _timelineTracker.Entries;

    public int PlannedStepCount => _deploymentOrchestrator.PlannedSteps.Count;
    public string CompletionInstructionText => _rebootPolicy.AutomaticRebootEnabled && !_isDebugSafeMode
        ? Format("Success.RebootCountdownFormat", RebootCountdownSeconds)
        : GetString("Success.ManualRebootInstruction");

    public string FailedStepText => Format("Error.FailedStepFormat", FailedStepName);

    public void ConfigureRebootPolicy(DeploymentRebootPolicy rebootPolicy)
    {
        ArgumentNullException.ThrowIfNull(rebootPolicy);
        _rebootPolicy = rebootPolicy;
        RebootCountdownSeconds = rebootPolicy.DelaySeconds;
        OnPropertyChanged(nameof(CompletionInstructionText));
    }

    public void SetComputerName(string computerName)
    {
        ComputerNameText = computerName;
    }

    public void CompleteStartupInitialization()
    {
        IsStartupInitializing = false;
        CurrentPage = DeploymentPage.Splash;
    }

    public void ShowWizard()
    {
        if (IsStartupInitializing)
        {
            return;
        }

        CurrentPage = DeploymentPage.Wizard;
    }

    public void BeginDeployment(string computerName, int plannedStepCount)
    {
        _isDeploymentInProgress = true;
        _lastLogsDirectoryPath = string.Empty;
        ClearFailureDetails();
        _plannedStepCount = plannedStepCount;
        _activeStepIndex = 0;
        _timelineTracker.Reset(_deploymentOrchestrator.PlannedSteps);

        DeploymentProgress = 0;
        UpdateGlobalProgressVisuals(0);
        SetCurrentStepName("Preparing deployment...");
        CurrentStepProgress = 0;
        IsCurrentStepProgressIndeterminate = true;
        SetCurrentStepProgressText("Waiting for progress...");
        StepCounterText = BuildStepCounterText(0);
        ComputerNameText = computerName;
        CaptureNetworkSnapshot();

        _deploymentStartTimeUtc = DateTimeOffset.Now;
        StartTimeText = FormatDeploymentStartTime(_deploymentStartTimeUtc.Value);
        ElapsedTimeText = "00:00:00";
        StartElapsedTimeTracking();

        CurrentPage = DeploymentPage.Progress;
    }

    public void CompleteDeployment(string? logsDirectoryPath)
    {
        _isDeploymentInProgress = false;
        _lastLogsDirectoryPath = logsDirectoryPath ?? string.Empty;
        _timelineTracker.CompleteAll();
        _activeStepIndex = _plannedStepCount;
        StepCounterText = BuildStepCounterText(_activeStepIndex);
        CurrentPage = DeploymentPage.Success;
    }

    public void FailDeployment(string? stepName, string? errorMessage, string? logsDirectoryPath = null)
    {
        _isDeploymentInProgress = false;
        _lastLogsDirectoryPath = logsDirectoryPath ?? _lastLogsDirectoryPath;
        SetFailureDetails(stepName, errorMessage);
        _timelineTracker.FailAt(_activeStepIndex);
        CurrentPage = DeploymentPage.Error;
    }

    public void ApplyExecutionRunResult(DeploymentExecutionRunResult executionRunResult)
    {
        ArgumentNullException.ThrowIfNull(executionRunResult);

        if (executionRunResult.IsSuccess)
        {
            CompleteDeployment(executionRunResult.LogsDirectoryPath);
            return;
        }

        string fallbackStep = string.IsNullOrWhiteSpace(FailedStepName)
            ? CurrentStepName
            : FailedStepName;
        string fallbackMessage = string.IsNullOrWhiteSpace(FailedStepErrorMessage)
            ? executionRunResult.Message
            : FailedStepErrorMessage;

        FailDeployment(fallbackStep, fallbackMessage, executionRunResult.LogsDirectoryPath);
    }

    public void ShowDebugProgress(string computerName, int currentStepIndex, int plannedStepCount, string currentStepName, int progressPercent)
    {
        _isDeploymentInProgress = false;
        ClearFailureDetails();
        _plannedStepCount = plannedStepCount;
        _activeStepIndex = currentStepIndex;
        SeedDebugTimeline(currentStepIndex, DeploymentStepState.Running);
        DeploymentProgress = progressPercent;
        UpdateGlobalProgressVisuals(progressPercent);
        ComputerNameText = computerName;
        SetCurrentStepName(currentStepName);
        StepCounterText = BuildStepCounterText(currentStepIndex);
        CurrentStepProgress = 65;
        IsCurrentStepProgressIndeterminate = false;
        SetCurrentStepProgressText("Applying image: 65%");
        CaptureNetworkSnapshot();
        _deploymentStartTimeUtc = DateTimeOffset.Now;
        StartTimeText = FormatDeploymentStartTime(_deploymentStartTimeUtc.Value);
        ElapsedTimeText = "00:00:00";
        StartElapsedTimeTracking();
        CurrentPage = DeploymentPage.Progress;
    }

    public void ShowDebugSuccess(string computerName, int plannedStepCount, string finalStepName)
    {
        _isDeploymentInProgress = false;
        StopElapsedTimeTracking();
        ClearFailureDetails();
        _plannedStepCount = plannedStepCount;
        _activeStepIndex = plannedStepCount;
        SeedDebugTimeline(plannedStepCount, DeploymentStepState.Succeeded);
        _timelineTracker.CompleteAll();
        DeploymentProgress = 100;
        UpdateGlobalProgressVisuals(100);
        ComputerNameText = computerName;
        SetCurrentStepName(finalStepName);
        StepCounterText = BuildStepCounterText(plannedStepCount);
        CurrentStepProgress = 100;
        IsCurrentStepProgressIndeterminate = false;
        SetCurrentStepProgressText("Step completed.");
        CurrentPage = DeploymentPage.Success;
    }

    public void ShowDebugError(string computerName, int currentStepIndex, string failedStepName, string failedStepErrorMessage)
    {
        _isDeploymentInProgress = false;
        StopElapsedTimeTracking();
        ComputerNameText = computerName;
        _plannedStepCount = _deploymentOrchestrator.PlannedSteps.Count;
        _activeStepIndex = currentStepIndex;
        SeedDebugTimeline(currentStepIndex, DeploymentStepState.Failed);
        StepCounterText = BuildStepCounterText(currentStepIndex);
        SetFailureDetails(failedStepName, failedStepErrorMessage);
        CurrentPage = DeploymentPage.Error;
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        try
        {
            LogPersistenceResult persistenceResult = FoundryDeployLogging.PersistCurrentLogs();
            if (persistenceResult.FailedFileCount > 0)
            {
                _logger.LogWarning(
                    "The durable deployment log snapshot could not be fully refreshed. CopiedFileCount={CopiedFileCount}, FailedFileCount={FailedFileCount}",
                    persistenceResult.CopiedFileCount,
                    persistenceResult.FailedFileCount);
            }

            string logFilePath = ResolveEffectiveLogFilePath();
            if (!File.Exists(logFilePath))
            {
                _logger.LogWarning("The deployment log file is unavailable. LogFilePath={LogFilePath}", logFilePath);
                return;
            }

            ProcessStartInfo startInfo = new("notepad.exe")
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(logFilePath);
            _ = Process.Start(startInfo);
            _logger.LogInformation("Opened log file at {LogFilePath}.", logFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open log file.");
        }
    }

    [RelayCommand]
    private Task ExportDiagnosticsAsync()
    {
        return ExportDiagnosticsAsync(SupportBundlePrivacyMode.Sanitized);
    }

    [RelayCommand]
    private async Task ExportRawDiagnosticsAsync()
    {
        var confirmationDialog = new LocalizedMessageDialog(
            GetString("Diagnostics.ExportRawTitle"),
            GetString("Diagnostics.ExportRawWarning"),
            GetString("Diagnostics.ExportRawConfirm"),
            GetString("Common.Cancel"));
        if (confirmationDialog.ShowDialog() != true)
        {
            return;
        }

        await ExportDiagnosticsAsync(SupportBundlePrivacyMode.Raw);
    }

    private async Task ExportDiagnosticsAsync(SupportBundlePrivacyMode privacyMode)
    {
        try
        {
            string? destinationDirectoryPath = SelectExportDestination();
            if (destinationDirectoryPath is null)
            {
                return;
            }

            _logger.LogInformation("Support bundle export started. PrivacyMode={PrivacyMode}", privacyMode);
            LogPersistenceResult persistenceResult = FoundryDeployLogging.PersistCurrentLogs();
            if (persistenceResult.FailedFileCount > 0)
            {
                _logger.LogWarning(
                    "The durable deployment log snapshot could not be fully refreshed before export. CopiedFileCount={CopiedFileCount}, FailedFileCount={FailedFileCount}",
                    persistenceResult.CopiedFileCount,
                    persistenceResult.FailedFileCount);
            }

            string[] logFilePaths = EnumerateSupportLogFiles();
            SupportBundleResult result = await new SupportBundleExporter().ExportAsync(
                new SupportBundleRequest
                {
                    ApplicationName = "Foundry.Deploy",
                    ApplicationVersion = FoundryDeployApplicationInfo.Version,
                    SessionId = DiagnosticSessionContext.CurrentSessionId,
                    DestinationDirectoryPath = destinationDirectoryPath,
                    LogFilePaths = logFilePaths,
                    PrivacyMode = privacyMode,
                    Summary = new Dictionary<string, string>
                    {
                        ["Architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                        ["DeploymentMode"] = Environment.GetEnvironmentVariable("FOUNDRY_DEPLOYMENT_MODE") ?? "Unknown",
                        ["OperatingSystem"] = Environment.OSVersion.VersionString,
                        ["Page"] = CurrentPage.ToString()
                    }
                });

            _logger.LogInformation(
                "Support bundle export completed. PrivacyMode={PrivacyMode}, IncludedFileCount={IncludedFileCount}, OmittedFileCount={OmittedFileCount}",
                privacyMode,
                result.IncludedFiles.Count,
                result.OmittedFiles.Count);
            _ = new LocalizedMessageDialog(
                GetString("Diagnostics.ExportSucceededTitle"),
                Format("Diagnostics.ExportSucceededMessageFormat", result.ArchivePath),
                GetString("Common.Close")).ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Support bundle export failed. PrivacyMode={PrivacyMode}", privacyMode);
            _ = new LocalizedMessageDialog(
                GetString("Diagnostics.ExportFailedTitle"),
                GetString("Diagnostics.ExportFailedMessage"),
                GetString("Common.Close")).ShowDialog();
        }
    }

    private string? SelectExportDestination()
    {
        if (WinPeRuntimeDetector.IsWinPeRuntime())
        {
            _logger.LogInformation("The shell folder picker was skipped because the application is running in WinPE.");
            return ResolveRequiredExternalExportDirectory();
        }

        try
        {
            string? externalExportDirectory = ResolveExternalExportDirectory();
            var picker = new OpenFolderDialog
            {
                Title = GetString("Diagnostics.ExportDestinationTitle"),
                InitialDirectory = ResolveSuggestedExportDirectory(externalExportDirectory)
            };
            return picker.ShowDialog() == true ? picker.FolderName : null;
        }
        catch (COMException ex) when (ex.HResult == ShellFolderPickerUnavailableHResult)
        {
            string? fallbackDirectoryPath = ResolveExternalExportDirectory();
            _logger.LogWarning(
                ex,
                "The shell folder picker is unavailable. Falling back to an external volume. HasFallbackDestination={HasFallbackDestination}",
                fallbackDirectoryPath is not null);
            return fallbackDirectoryPath
                   ?? throw new IOException("No ready external volume is available for the diagnostic export.");
        }
    }

    private string ResolveRequiredExternalExportDirectory()
    {
        return ResolveExternalExportDirectory()
               ?? throw new IOException("No ready external volume is available for the diagnostic export.");
    }

    private string[] EnumerateSupportLogFiles()
    {
        string? activeDirectoryPath = Path.GetDirectoryName(FoundryDeployLogging.CurrentLogFilePath);
        return new[] { activeDirectoryPath, _lastLogsDirectoryPath }
            .Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .SelectMany(static path => Directory.GetFiles(path!, "Foundry*.log", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveSuggestedExportDirectory(string? externalExportDirectory)
    {
        try
        {
            return externalExportDirectory
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
    }

    private string? ResolveExternalExportDirectory()
    {
        return SupportBundleDestinationPolicy.SelectExternalDestination(
            new WindowsVolumeDiscovery().GetVolumes());
    }

    [RelayCommand(CanExecute = nameof(CanRebootNow))]
    private async Task RebootNowAsync()
    {
        await ExecuteRebootAsync().ConfigureAwait(false);
    }

    private void OnOperationProgressChanged(object? sender, EventArgs e)
    {
        RunOnUi(() =>
        {
            if (!_isDeploymentInProgress &&
                !_operationProgressService.IsOperationInProgress &&
                string.IsNullOrWhiteSpace(_operationProgressService.Status))
            {
                return;
            }

            int normalizedProgress = Math.Clamp(_operationProgressService.Progress, 0, 100);
            DeploymentProgress = Math.Max(DeploymentProgress, normalizedProgress);
            UpdateGlobalProgressVisuals(DeploymentProgress);

        });
    }

    private void OnStepProgressChanged(object? sender, DeploymentStepProgress stepProgress)
    {
        RunOnUi(() =>
        {
            if (stepProgress.StepIndex != _activeStepIndex)
            {
                _activeStepIndex = stepProgress.StepIndex;
                CurrentStepProgress = 0;
                IsCurrentStepProgressIndeterminate = true;
                SetCurrentStepProgressText("Starting step...");
            }

            SetCurrentStepName(stepProgress.StepName);
            StepCounterText = BuildStepCounterText(stepProgress.StepIndex);
            _timelineTracker.Apply(stepProgress);

            DeploymentProgress = Math.Max(DeploymentProgress, stepProgress.ProgressPercent);
            UpdateGlobalProgressVisuals(DeploymentProgress);
            UpdateCurrentStepProgressVisuals(stepProgress);

            if (stepProgress.State == DeploymentStepState.Failed)
            {
                SetFailureDetails(stepProgress.StepName, stepProgress.Message ?? "Step failed.");
            }
        });
    }

    public override void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _operationProgressService.ProgressChanged -= OnOperationProgressChanged;
        _deploymentOrchestrator.StepProgressChanged -= OnStepProgressChanged;
        LocalizationService.LanguageChanged -= OnLocalizationLanguageChanged;
        StopElapsedTimeTracking();
        StopRebootCountdown(resetSeconds: false);
        _isDisposed = true;
        base.Dispose();
    }

    partial void OnCurrentPageChanged(DeploymentPage value)
    {
        if (value == DeploymentPage.Success)
        {
            StartConfiguredReboot();
        }
        else
        {
            StopRebootCountdown(resetSeconds: true);
        }

        if (value != DeploymentPage.Progress && !_isDeploymentInProgress)
        {
            StopElapsedTimeTracking();
        }

        RebootNowCommand.NotifyCanExecuteChanged();
    }

    private void UpdateGlobalProgressVisuals(int progressValue)
    {
        int clampedProgress = Math.Clamp(progressValue, 0, 100);
        IsGlobalProgressIndeterminate = _isDeploymentInProgress && clampedProgress <= 0;
    }

    private void UpdateCurrentStepProgressVisuals(DeploymentStepProgress stepProgress)
    {
        if (stepProgress.State == DeploymentStepState.Succeeded)
        {
            CurrentStepProgress = 100;
            IsCurrentStepProgressIndeterminate = false;
            SetCurrentStepProgressText(stepProgress.StepSubProgressLabel ?? "Step completed.");
            return;
        }

        if (stepProgress.State == DeploymentStepState.Failed)
        {
            IsCurrentStepProgressIndeterminate = false;
            SetCurrentStepProgressText(stepProgress.Message ?? "Step failed.");
            return;
        }

        if (stepProgress.State == DeploymentStepState.Skipped)
        {
            IsCurrentStepProgressIndeterminate = false;
            SetCurrentStepProgressText(stepProgress.Message ?? "Step skipped.");
            return;
        }

        if (stepProgress.StepSubProgressPercent.HasValue)
        {
            double normalized = Math.Clamp(stepProgress.StepSubProgressPercent.Value, 0d, 100d);
            CurrentStepProgress = normalized;
            IsCurrentStepProgressIndeterminate = false;
            SetCurrentStepProgressText(string.IsNullOrWhiteSpace(stepProgress.StepSubProgressLabel)
                ? $"{normalized:0.#}%"
                : stepProgress.StepSubProgressLabel!);
            return;
        }

        if (stepProgress.StepSubProgressIndeterminate)
        {
            IsCurrentStepProgressIndeterminate = true;
            SetCurrentStepProgressText(stepProgress.StepSubProgressLabel ?? "In progress...");
        }
    }

    private void StartElapsedTimeTracking()
    {
        StopElapsedTimeTracking();
        _elapsedTimeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimeTimer.Tick += OnElapsedTimeTick;
        _elapsedTimeTimer.Start();
    }

    private void StopElapsedTimeTracking()
    {
        if (_elapsedTimeTimer is null)
        {
            return;
        }

        _elapsedTimeTimer.Tick -= OnElapsedTimeTick;
        _elapsedTimeTimer.Stop();
        _elapsedTimeTimer = null;
    }

    private void OnElapsedTimeTick(object? sender, EventArgs e)
    {
        if (!_deploymentStartTimeUtc.HasValue)
        {
            return;
        }

        TimeSpan elapsed = DateTimeOffset.Now - _deploymentStartTimeUtc.Value;
        ElapsedTimeText = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatDeploymentStartTime(DateTimeOffset startTime)
    {
        return startTime.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
    }

    private void StartConfiguredReboot()
    {
        StopRebootCountdown(resetSeconds: false);
        RebootCountdownSeconds = _rebootPolicy.DelaySeconds;
        if (_isDebugSafeMode)
        {
            return;
        }

        switch (_rebootPolicy.Action)
        {
            case DeploymentRebootAction.WaitForManualReboot:
                return;
            case DeploymentRebootAction.RebootImmediately:
                _ = ExecuteRebootAsync();
                return;
            case DeploymentRebootAction.StartCountdown:
                break;
            default:
                throw new InvalidOperationException($"Unsupported reboot action: {_rebootPolicy.Action}.");
        }

        _rebootCountdownTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _rebootCountdownTimer.Tick += OnRebootCountdownTick;
        _rebootCountdownTimer.Start();
    }

    private void StopRebootCountdown(bool resetSeconds)
    {
        if (_rebootCountdownTimer is not null)
        {
            _rebootCountdownTimer.Tick -= OnRebootCountdownTick;
            _rebootCountdownTimer.Stop();
            _rebootCountdownTimer = null;
        }

        if (resetSeconds)
        {
            RebootCountdownSeconds = _rebootPolicy.DelaySeconds;
        }
    }

    private void OnRebootCountdownTick(object? sender, EventArgs e)
    {
        if (RebootCountdownSeconds > 0)
        {
            RebootCountdownSeconds--;
        }

        if (RebootCountdownSeconds > 0)
        {
            return;
        }

        StopRebootCountdown(resetSeconds: false);
        if (!_isDebugSafeMode)
        {
            _ = ExecuteRebootAsync();
        }
    }

    private bool CanRebootNow()
    {
        return IsSuccessPage && !_isDebugSafeMode && !_isRebootInProgress;
    }

    private async Task ExecuteRebootAsync()
    {
        if (_isDebugSafeMode || _isRebootInProgress)
        {
            return;
        }

        _isRebootInProgress = true;
        RebootNowCommand.NotifyCanExecuteChanged();
        StopRebootCountdown(resetSeconds: false);

        try
        {
            string rebootExecutablePath = Path.Combine(Environment.SystemDirectory, "wpeutil.exe");
            if (!File.Exists(rebootExecutablePath))
            {
                throw new FileNotFoundException("Required reboot executable 'wpeutil.exe' was not found.", rebootExecutablePath);
            }

            ProcessExecutionResult result = await _processRunner
                .RunAsync(rebootExecutablePath, ["Reboot"], Path.GetTempPath(), executionTimeout: TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                return;
            }

            RunOnUi(() =>
            {
                string diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                SetFailureDetails("System reboot", $"wpeutil.exe failed with exit code {result.ExitCode}. {diagnostic}".Trim());
                CurrentPage = DeploymentPage.Error;
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                SetFailureDetails("System reboot", ex.Message);
                CurrentPage = DeploymentPage.Error;
            });
        }
        finally
        {
            RunOnUi(() =>
            {
                _isRebootInProgress = false;
                RebootNowCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void SetFailureDetails(string? stepName, string? errorMessage)
    {
        _rawFailedStepName = string.IsNullOrWhiteSpace(stepName) ? "Unknown step" : stepName;
        _rawFailedStepErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? "No error details were provided." : errorMessage;
        FailedStepName = DeploymentUiTextLocalizer.LocalizeStepName(_rawFailedStepName);
        FailedStepErrorMessage = DeploymentUiTextLocalizer.LocalizeMessage(_rawFailedStepErrorMessage);
    }

    private void ClearFailureDetails()
    {
        _rawFailedStepName = string.Empty;
        _rawFailedStepErrorMessage = string.Empty;
        FailedStepName = string.Empty;
        FailedStepErrorMessage = string.Empty;
    }

    private void CaptureNetworkSnapshot()
    {
        string notAvailable = GetString("Common.NotAvailable");
        IpAddress = notAvailable;
        SubnetMask = notAvailable;
        GatewayAddress = notAvailable;
        MacAddress = notAvailable;

        try
        {
            var snapshot = CreateNetworkSnapshot(
                _networkAdapterSnapshotProvider.GetAdapters());
            if (snapshot is null)
            {
                return;
            }

            IpAddress = snapshot.Value.IpAddress;
            SubnetMask = string.IsNullOrWhiteSpace(snapshot.Value.SubnetMask)
                ? notAvailable
                : snapshot.Value.SubnetMask;
            GatewayAddress = snapshot.Value.GatewayAddress ?? notAvailable;
            MacAddress = string.IsNullOrWhiteSpace(snapshot.Value.MacAddress)
                ? notAvailable
                : snapshot.Value.MacAddress;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve network snapshot for deployment session.");
        }
    }

    internal static NetworkAdapterSnapshot? SelectNetworkAdapter(
        IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        return adapters.FirstOrDefault(static adapter =>
            adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.InterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
            adapter.Ipv4Addresses.Count > 0);
    }

    internal static (string IpAddress, string? SubnetMask, string? GatewayAddress, string MacAddress)?
        CreateNetworkSnapshot(IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        NetworkAdapterSnapshot? adapter = SelectNetworkAdapter(adapters);
        if (adapter is null)
        {
            return null;
        }

        NetworkIpv4AddressSnapshot address = adapter.Ipv4Addresses[0];
        return (
            address.Address,
            address.SubnetMask,
            adapter.Gateways.FirstOrDefault(),
            adapter.MacAddress);
    }

    private string BuildStepCounterText(int currentStep)
    {
        if (_plannedStepCount <= 0)
        {
            return GetString("Status.StepCounterUnknown");
        }

        int normalizedStep = Math.Clamp(currentStep, 0, _plannedStepCount);
        return Format("Status.StepCounterFormat", normalizedStep, _plannedStepCount);
    }

    private string ResolveEffectiveLogFilePath()
    {
        if (!string.IsNullOrWhiteSpace(_lastLogsDirectoryPath))
        {
            string persistedLogFilePath = Path.Combine(_lastLogsDirectoryPath, FoundryDeployLogging.LogFileName);
            if (File.Exists(persistedLogFilePath))
            {
                return persistedLogFilePath;
            }
        }

        return FoundryDeployLogging.CurrentLogFilePath;
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

    private void SetCurrentStepName(string value)
    {
        _rawCurrentStepName = value;
        CurrentStepName = DeploymentUiTextLocalizer.LocalizeStepName(value);
    }

    private void SetCurrentStepProgressText(string value)
    {
        _rawCurrentStepProgressText = value;
        CurrentStepProgressText = DeploymentUiTextLocalizer.LocalizeMessage(value);
    }

    private void OnLocalizationLanguageChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            CurrentStepName = DeploymentUiTextLocalizer.LocalizeStepName(_rawCurrentStepName);
            CurrentStepProgressText = DeploymentUiTextLocalizer.LocalizeMessage(_rawCurrentStepProgressText);
            FailedStepName = string.IsNullOrWhiteSpace(_rawFailedStepName)
                ? string.Empty
                : DeploymentUiTextLocalizer.LocalizeStepName(_rawFailedStepName);
            FailedStepErrorMessage = string.IsNullOrWhiteSpace(_rawFailedStepErrorMessage)
                ? string.Empty
                : DeploymentUiTextLocalizer.LocalizeMessage(_rawFailedStepErrorMessage);
            StepCounterText = BuildStepCounterText(_activeStepIndex);
            OnPropertyChanged(nameof(CompletionInstructionText));
            CaptureNetworkSnapshot();
            _timelineTracker.RefreshLocalization();
        });
    }

    private void SeedDebugTimeline(int currentStepIndex, DeploymentStepState currentState)
    {
        _timelineTracker.Reset(_deploymentOrchestrator.PlannedSteps);
        int normalizedIndex = Math.Clamp(currentStepIndex, 0, _timelineTracker.Entries.Count);
        for (int index = 0; index < normalizedIndex - 1; index++)
        {
            _timelineTracker.SetState(index + 1, DeploymentStepState.Succeeded);
        }

        if (normalizedIndex > 0)
        {
            _timelineTracker.SetState(normalizedIndex, currentState);
        }
    }

    private string GetString(string key)
    {
        return Strings[key];
    }

    private string LocalizeTimelineState(DeploymentStepState state)
    {
        return state switch
        {
            DeploymentStepState.Running => GetString("Status.InProgress"),
            DeploymentStepState.Succeeded => GetString("Status.StepCompleted"),
            DeploymentStepState.Skipped => GetString("Status.StepSkipped"),
            DeploymentStepState.Failed => GetString("Status.StepFailed"),
            _ => GetString("Status.WaitingForProgress")
        };
    }

    private string Format(string key, params object[] args)
    {
        return string.Format(LocalizationService.CurrentCulture, GetString(key), args);
    }
}
