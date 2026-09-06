// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Operations;
using Foundry.Utilities.Progress;
using Serilog;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>
/// Provides shared state, logging, workspace paths, and progress helpers to deployment steps.
/// </summary>
public sealed class DeploymentStepExecutionContext : IDisposable
{
    /// <summary>Holds credential-bearing bytes only in memory between validation and staging.</summary>
    internal Unattend.UnattendSnapshot? UnattendSnapshot { get; set; }

    /// <summary>Releases sensitive answer-file content on every terminal deployment outcome.</summary>
    public void Dispose()
    {
        UnattendSnapshot?.Dispose();
        UnattendSnapshot = null;
    }

    private const string WinPeRoot = @"X:\Foundry";
    private static readonly string WinPeDriveRoot = Path.GetPathRoot(WinPeRoot) ?? @"X:\";
    private const string LogsFolderName = "Logs";
    private const string TempFolderName = "Temp";
    private const string StateFolderName = "State";
    private const string RuntimeFolderName = "Runtime";
    private const string CacheFolderName = "Cache";
    private const string OperatingSystemsFolderName = "OperatingSystems";
    private const string DriverPacksFolderName = "DriverPacks";
    private const string MicrosoftUpdateCatalogFolderName = "MicrosoftUpdateCatalog";
    private const string DriversFolderName = "Drivers";
    private const string FirmwareFolderName = "Firmware";
    private const string DryRunWorkspaceFolderName = "DryRun";
    private const string RuntimeWorkspaceFolderName = "Runtime";
    private const long UnknownTotalDownloadProgressIncrementBytes = 16L * 1024 * 1024;

    private readonly IOperationProgressService _operationProgressService;
    private readonly IDeploymentLogService _deploymentLogService;
    private readonly ITargetDiskService _targetDiskService;
    private readonly Action<DeploymentStepProgress> _emitStepProgress;
    private readonly object _runtimeStatePersistenceLock = new();
    private Task _pendingRuntimeStatePersistence = Task.CompletedTask;

    /// <summary>
    /// Initializes a deployment step execution context and creates the initial log session.
    /// </summary>
    public DeploymentStepExecutionContext(
        DeploymentContext request,
        DeploymentRuntimeState runtimeState,
        IReadOnlyList<string> plannedSteps,
        IOperationProgressService operationProgressService,
        IDeploymentLogService deploymentLogService,
        ITargetDiskService targetDiskService,
        Action<DeploymentStepProgress> emitStepProgress)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        RuntimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        PlannedSteps = plannedSteps ?? throw new ArgumentNullException(nameof(plannedSteps));
        _operationProgressService = operationProgressService ?? throw new ArgumentNullException(nameof(operationProgressService));
        _deploymentLogService = deploymentLogService ?? throw new ArgumentNullException(nameof(deploymentLogService));
        _targetDiskService = targetDiskService ?? throw new ArgumentNullException(nameof(targetDiskService));
        _emitStepProgress = emitStepProgress ?? throw new ArgumentNullException(nameof(emitStepProgress));

        EnsureWorkspaceFolders();
        LogSession = _deploymentLogService.Initialize(RuntimeState.WorkspaceRoot);
    }

    /// <summary>
    /// Gets the immutable deployment request.
    /// </summary>
    public DeploymentContext Request { get; }

    /// <summary>
    /// Gets mutable runtime state persisted between deployment steps.
    /// </summary>
    public DeploymentRuntimeState RuntimeState { get; }

    /// <summary>
    /// Gets the planned step names in execution order.
    /// </summary>
    public IReadOnlyList<string> PlannedSteps { get; }

    /// <summary>
    /// Gets the active deployment log session.
    /// </summary>
    public DeploymentLogSession LogSession { get; private set; }

    /// <summary>
    /// Gets the one-based index of the currently executing step.
    /// </summary>
    public int StepIndex { get; private set; }

    /// <summary>
    /// Gets the total number of planned deployment steps.
    /// </summary>
    public int StepCount { get; private set; }

    /// <summary>
    /// Gets the name of the currently executing step.
    /// </summary>
    public string StepName { get; private set; } = string.Empty;

    /// <summary>
    /// Resolves the workspace root for WinPE, dry-run, or local runtime execution.
    /// </summary>
    /// <param name="context">The deployment request.</param>
    /// <returns>The workspace root path.</returns>
    public static string ResolveWorkspaceRoot(DeploymentContext context)
    {
        bool hasWinPeDrive = Directory.Exists(WinPeDriveRoot);
        if (hasWinPeDrive)
        {
            // Real WinPE runs use X:\Foundry so logs and transient files stay with the boot environment.
            return WinPeRoot;
        }

        string modeFolder = context.IsDryRun ? DryRunWorkspaceFolderName : RuntimeWorkspaceFolderName;
        return Path.Combine(Path.GetTempPath(), "Foundry", modeFolder);
    }

    /// <summary>
    /// Updates the current step metadata before a deployment step runs.
    /// </summary>
    /// <param name="step">Step that is about to execute.</param>
    /// <param name="stepIndex">One-based step index.</param>
    public void SetCurrentStep(IDeploymentStep step, int stepIndex)
    {
        ArgumentNullException.ThrowIfNull(step);

        StepName = step.Name;
        StepIndex = stepIndex;
        StepCount = PlannedSteps.Count;
        RuntimeState.CurrentStep = step.Name;
        RuntimeState.CurrentOperation = DeploymentOperationNames.ForStep(step.Name);
    }

    /// <summary>
    /// Updates the active logical operation used for failure telemetry.
    /// </summary>
    /// <param name="operationName">Stable logical operation name.</param>
    public void SetCurrentOperation(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        RuntimeState.CurrentOperation = operationName;
        QueueRuntimeStatePersistence();
    }

    /// <summary>
    /// Emits the current step state and optional nested progress to subscribers.
    /// </summary>
    /// <param name="state">Current state of the deployment step.</param>
    /// <param name="message">Optional primary progress message.</param>
    /// <param name="stepSubProgressPercent">Optional nested step progress percentage.</param>
    /// <param name="stepSubProgressIndeterminate">Whether nested progress should be shown as indeterminate.</param>
    /// <param name="stepSubProgressLabel">Optional nested progress label.</param>
    public void EmitCurrentStep(
        DeploymentStepState state,
        string? message,
        double? stepSubProgressPercent = null,
        bool stepSubProgressIndeterminate = true,
        string? stepSubProgressLabel = null)
    {
        int progressPercent = CalculateStepProgressPercent(StepIndex, StepCount);
        _emitStepProgress(new DeploymentStepProgress
        {
            StepName = StepName,
            State = state,
            StepIndex = StepIndex,
            StepCount = StepCount,
            ProgressPercent = progressPercent,
            Message = message,
            StepSubProgressPercent = stepSubProgressPercent,
            StepSubProgressIndeterminate = stepSubProgressIndeterminate,
            StepSubProgressLabel = stepSubProgressLabel
        });
    }

    /// <summary>
    /// Emits the current step as running with an indeterminate nested progress label.
    /// </summary>
    /// <param name="stepMessage">Primary progress message.</param>
    /// <param name="stepSubProgressLabel">Nested indeterminate progress label.</param>
    /// <param name="operationName">Stable logical operation name.</param>
    public void EmitCurrentStepIndeterminate(
        string stepMessage,
        string stepSubProgressLabel,
        string operationName)
    {
        SetCurrentOperation(operationName);
        EmitCurrentStep(
            DeploymentStepState.Running,
            stepMessage,
            stepSubProgressPercent: null,
            stepSubProgressIndeterminate: true,
            stepSubProgressLabel: stepSubProgressLabel);
    }

    /// <summary>
    /// Reports shell-level progress for the current step.
    /// </summary>
    /// <param name="message">Progress message shown by the shell.</param>
    public void ReportCurrentStepProgress(string message)
    {
        _operationProgressService.Report(CalculateStepProgressPercent(StepIndex, StepCount), message);
    }

    /// <summary>
    /// Appends an entry to the active deployment log.
    /// </summary>
    /// <param name="level">Log level for the entry.</param>
    /// <param name="message">Log message.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>A task that completes after the log entry is persisted.</returns>
    public Task AppendLogAsync(
        DeploymentLogLevel level,
        string message,
        CancellationToken cancellationToken = default)
    {
        return _deploymentLogService.AppendAsync(LogSession, level, message, cancellationToken);
    }

    /// <summary>
    /// Persists the current runtime state into the active log session.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>A task that completes after state is persisted.</returns>
    public Task SaveRuntimeStateAsync(CancellationToken cancellationToken = default)
    {
        return _deploymentLogService.SaveStateAsync(LogSession, RuntimeState, cancellationToken);
    }

    /// <summary>
    /// Attempts to persist runtime state without replacing the primary deployment outcome on failure.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>A task that completes after the best-effort persistence attempt.</returns>
    public async Task TrySaveRuntimeStateAsync(CancellationToken cancellationToken = default)
    {
        Task persistence;
        lock (_runtimeStatePersistenceLock)
        {
            persistence = PersistRuntimeStateAfterAsync(_pendingRuntimeStatePersistence, cancellationToken);
            _pendingRuntimeStatePersistence = ObserveRuntimeStatePersistenceAsync(persistence);
        }

        await persistence.ConfigureAwait(false);
    }

    private void QueueRuntimeStatePersistence()
    {
        lock (_runtimeStatePersistenceLock)
        {
            Task persistence = PersistRuntimeStateAfterAsync(
                _pendingRuntimeStatePersistence,
                CancellationToken.None);
            _pendingRuntimeStatePersistence = ObserveRuntimeStatePersistenceAsync(persistence);
        }
    }

    private async Task PersistRuntimeStateAfterAsync(
        Task previousPersistence,
        CancellationToken cancellationToken)
    {
        await previousPersistence.ConfigureAwait(false);
        try
        {
            await SaveRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRuntimeStatePersistenceFailure(ex);
        }
    }

    private static async Task ObserveRuntimeStatePersistenceAsync(Task persistence)
    {
        try
        {
            await persistence.ConfigureAwait(false);
        }
        catch
        {
            // Preserve the caller-visible outcome on the original task while keeping the serialized queue recoverable.
        }
    }

    private void LogRuntimeStatePersistenceFailure(Exception exception)
    {
        Log.ForContext<DeploymentStepExecutionContext>().Warning(
            exception,
            "Deployment runtime state could not be persisted. Workflow={Workflow}, Stage={Stage}, StepName={StepName}, OperationId={OperationId}, FailedOperationName={FailedOperationName}",
            "deployment",
            "runtime_state_persistence",
            RuntimeState.CurrentStep,
            RuntimeState.OperationId,
            RuntimeState.CurrentOperation);
    }

    /// <summary>
    /// Moves the active log session to the target Windows Foundry root after the target partition is available.
    /// </summary>
    /// <param name="targetFoundryRoot">Foundry root on the applied Windows partition.</param>
    /// <param name="cancellationToken">Token that cancels the transfer log write.</param>
    /// <returns>A task that completes after the log session is rebound.</returns>
    public Task RebindLogSessionToTargetAsync(
        string targetFoundryRoot,
        CancellationToken cancellationToken = default)
    {
        DeploymentLogSession previousSession = LogSession;
        DeploymentLogSession rebound;
        try
        {
            rebound = _deploymentLogService.Initialize(targetFoundryRoot);
        }
        catch (Exception ex)
        {
            Log.ForContext<DeploymentStepExecutionContext>().Warning(
                ex,
                "Deployment log persistence destination is unavailable. SourceRootPath={SourceRootPath}, TargetRootPath={TargetRootPath}",
                previousSession.RootPath,
                targetFoundryRoot);
            return Task.CompletedTask;
        }

        LogSession = rebound;
        FoundryDeployLogging.RegisterPersistenceDirectory(rebound.LogsDirectoryPath);
        Log.ForContext<DeploymentStepExecutionContext>().Information(
            "Deployment log persistence destination changed. SourceRootPath={SourceRootPath}, TargetRootPath={TargetRootPath}",
            previousSession.RootPath,
            targetFoundryRoot);

        LogPersistenceResult persistenceResult = FoundryDeployLogging.PersistCurrentLogs();
        if (persistenceResult.FailedFileCount > 0)
        {
            Log.ForContext<DeploymentStepExecutionContext>().Warning(
                "One or more deployment log files could not be persisted. CopiedFileCount={CopiedFileCount}, FailedFileCount={FailedFileCount}, TargetLogsDirectoryPath={TargetLogsDirectoryPath}",
                persistenceResult.CopiedFileCount,
                persistenceResult.FailedFileCount,
                rebound.LogsDirectoryPath);
        }

        try
        {
            CopyDirectoryContents(previousSession.StateDirectoryPath, rebound.StateDirectoryPath);
        }
        catch (Exception ex)
        {
            Log.ForContext<DeploymentStepExecutionContext>().Warning(
                ex,
                "Deployment state could not be copied to the new diagnostic destination. SourceStateDirectoryPath={SourceStateDirectoryPath}, TargetStateDirectoryPath={TargetStateDirectoryPath}",
                previousSession.StateDirectoryPath,
                rebound.StateDirectoryPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Revalidates that the selected target disk is still present and selectable.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels disk enumeration.</param>
    /// <returns>The selected disk, or a failed step result when validation fails.</returns>
    public async Task<(TargetDiskInfo? SelectedDisk, DeploymentStepResult? Failure)> TryGetValidatedTargetDiskAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TargetDiskInfo> disks = await _targetDiskService.GetDisksAsync(cancellationToken).ConfigureAwait(false);
        TargetDiskInfo? selectedDisk = Request.ConfirmedTargetDisk?.Match(disks);
        if (selectedDisk is null || Request.ConfirmedTargetDisk?.DiskNumber != Request.TargetDiskNumber)
        {
            return (null, DeploymentStepResult.Failed(
                "The confirmed target disk is unavailable, changed, or unsafe.",
                DeploymentFailure.Guard(
                    DeploymentOperationNames.ValidateTargetDisk,
                    DeploymentFailureReasons.InvalidState,
                    "confirmed_target_disk_mismatch")));
        }
        await AppendLogAsync(DeploymentLogLevel.Info, $"Target disk revalidated: {selectedDisk.DisplayLabel}", cancellationToken).ConfigureAwait(false);
        return (selectedDisk, null);
    }

    /// <summary>
    /// Creates required workspace folders under the current runtime workspace root.
    /// </summary>
    public void EnsureWorkspaceFolders()
    {
        EnsureWorkspaceFolders(RuntimeState.WorkspaceRoot);
    }

    /// <summary>
    /// Resolves the logs folder path for the active workspace.
    /// </summary>
    /// <returns>The workspace logs path.</returns>
    public string ResolveWorkspaceLogsPath()
    {
        return Path.Combine(ResolveWorkspaceRoot(RuntimeState), LogsFolderName);
    }

    /// <summary>
    /// Resolves a path under the active workspace temporary folder.
    /// </summary>
    /// <param name="relativeSegments">Optional path segments below the temporary folder.</param>
    /// <returns>The resolved temporary path.</returns>
    public string ResolveWorkspaceTempPath(params string[] relativeSegments)
    {
        string currentPath = Path.Combine(ResolveWorkspaceRoot(RuntimeState), TempFolderName);
        foreach (string segment in relativeSegments)
        {
            currentPath = Path.Combine(currentPath, segment);
        }

        return currentPath;
    }

    /// <summary>
    /// Resolves the operating system cache root for the current deployment mode.
    /// </summary>
    /// <returns>The operating system cache root.</returns>
    public string ResolveOperatingSystemCacheRoot()
    {
        return ResolvePayloadCacheRoot(OperatingSystemsFolderName);
    }


    /// <summary>
    /// Resolves the driver pack cache root for the current deployment mode.
    /// </summary>
    /// <returns>The driver pack cache root.</returns>
    public string ResolveDriverPackCacheRoot()
    {
        return ResolvePayloadCacheRoot(DriverPacksFolderName);
    }


    public string ResolveMicrosoftUpdateCatalogDriverCacheRoot()
    {
        return Path.Combine(ResolvePayloadCacheRoot(MicrosoftUpdateCatalogFolderName), DriversFolderName);
    }

    public string ResolveMicrosoftUpdateCatalogFirmwareCacheRoot()
    {
        return Path.Combine(ResolvePayloadCacheRoot(MicrosoftUpdateCatalogFolderName), FirmwareFolderName);
    }

    /// <summary>
    /// Gets the target Foundry root or throws when the target partition has not been prepared.
    /// </summary>
    /// <returns>The target Foundry root path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the target Foundry root is unavailable.</exception>
    public string EnsureTargetFoundryRoot()
    {
        return RuntimeState.TargetFoundryRoot
            ?? throw new InvalidOperationException("Target Foundry root is unavailable.");
    }

    /// <summary>
    /// Creates a progress adapter that maps artifact download progress to shell and step progress.
    /// </summary>
    /// <param name="artifactLabel">User-facing artifact label.</param>
    /// <param name="operationName">Stable logical operation name.</param>
    /// <returns>A download progress reporter.</returns>
    public IProgress<DownloadProgress> CreateDownloadProgressReporter(
        string artifactLabel,
        string operationName)
    {
        SetCurrentOperation(operationName);
        double? lastReportedPercent = null;
        long nextUnknownTotalReportThreshold = 0;

        return new DelegateProgress<DownloadProgress>(progress =>
        {
            string details;
            double? stepSubProgressPercent = null;
            bool stepSubProgressIndeterminate = true;

            if (progress.TotalBytes is long totalBytes && totalBytes > 0)
            {
                double percent = TransferProgress.CalculatePercentage(progress.BytesDownloaded, totalBytes) ?? 0d;
                bool isFinal = progress.BytesDownloaded >= totalBytes;
                if (!isFinal &&
                    lastReportedPercent.HasValue &&
                    percent <= lastReportedPercent.Value)
                {
                    return;
                }

                lastReportedPercent = percent;
                details = $"{percent:0.#}% ({FormatByteSize(progress.BytesDownloaded)} / {FormatByteSize(totalBytes)})";
                stepSubProgressPercent = percent;
                stepSubProgressIndeterminate = false;
            }
            else
            {
                bool shouldReport = progress.BytesDownloaded == 0 ||
                                    progress.BytesDownloaded >= nextUnknownTotalReportThreshold;
                if (!shouldReport)
                {
                    return;
                }

                nextUnknownTotalReportThreshold = progress.BytesDownloaded + UnknownTotalDownloadProgressIncrementBytes;
                details = $"{FormatByteSize(progress.BytesDownloaded)} downloaded";
            }

            string stepMessage = $"Downloading {artifactLabel}...";
            ReportCurrentStepProgress(stepMessage);
            EmitCurrentStep(
                DeploymentStepState.Running,
                stepMessage,
                stepSubProgressPercent,
                stepSubProgressIndeterminate,
                details);
        });
    }

    /// <summary>
    /// Creates a progress adapter that emits monotonic nested step percentages.
    /// </summary>
    /// <param name="stepMessage">Primary step message.</param>
    /// <param name="stepLabelPrefix">Nested label prefix.</param>
    /// <returns>A percentage progress reporter.</returns>
    public IProgress<double> CreateStepPercentProgressReporter(string stepMessage, string stepLabelPrefix)
    {
        object progressSync = new();
        double lastReportedPercent = double.NaN;

        return new DelegateProgress<double>(percent =>
        {
            double normalized = Math.Clamp(percent, 0d, 100d);
            lock (progressSync)
            {
                if (!double.IsNaN(lastReportedPercent) && normalized <= lastReportedPercent)
                {
                    return;
                }

                lastReportedPercent = normalized;
            }

            EmitCurrentStep(
                DeploymentStepState.Running,
                stepMessage,
                stepSubProgressPercent: normalized,
                stepSubProgressIndeterminate: false,
                stepSubProgressLabel: $"{stepLabelPrefix}: {normalized:0.#}%");
        });
    }

    /// <summary>
    /// Resolves a safe artifact file name from a preferred name or source URL.
    /// </summary>
    /// <param name="preferredFileName">Preferred catalog file name.</param>
    /// <param name="sourceUrl">Source URL used when no preferred file name is available.</param>
    /// <returns>A safe file name for local storage.</returns>
    public static string ResolveFileName(string preferredFileName, string sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(preferredFileName))
        {
            return SanitizePathSegment(preferredFileName);
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri))
        {
            string fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return SanitizePathSegment(fileName);
            }
        }

        return $"artifact-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.bin";
    }

    /// <summary>
    /// Replaces invalid file-name characters in a path segment.
    /// </summary>
    /// <param name="value">Path segment to sanitize.</param>
    /// <returns>A non-empty path segment.</returns>
    public static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "item";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return sanitized.Trim().TrimEnd('.');
    }

    private string EnsureResolvedCache()
    {
        return RuntimeState.ResolvedCache?.RootPath
            ?? throw new InvalidOperationException("Cache strategy has not been resolved.");
    }

    private string EnsureCacheBaseRoot()
    {
        return ResolveCacheBaseRoot(EnsureResolvedCache());
    }

    private string ResolvePayloadCacheRoot(string payloadFolderName)
    {
        if (RuntimeState.Mode == DeploymentMode.Iso &&
            !string.IsNullOrWhiteSpace(RuntimeState.TargetFoundryRoot))
        {
            return Path.Combine(RuntimeState.TargetFoundryRoot, CacheFolderName, payloadFolderName);
        }

        return Path.Combine(EnsureCacheBaseRoot(), CacheFolderName, payloadFolderName);
    }

    /// <summary>Returns target storage only after the deployment has prepared its Windows partition.</summary>
    public string? ResolveTargetPayloadCacheRoot(string payloadFolderName)
    {
        return string.IsNullOrWhiteSpace(RuntimeState.TargetFoundryRoot)
            ? null : Path.Combine(RuntimeState.TargetFoundryRoot, CacheFolderName, payloadFolderName);
    }

    private static string ResolveWorkspaceRoot(DeploymentRuntimeState runtimeState)
    {
        return string.IsNullOrWhiteSpace(runtimeState.WorkspaceRoot)
            ? WinPeRoot
            : runtimeState.WorkspaceRoot;
    }

    private static void EnsureWorkspaceFolders(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new InvalidOperationException("Workspace root is required.");
        }

        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(Path.Combine(workspaceRoot, LogsFolderName));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, TempFolderName));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, StateFolderName));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, RuntimeFolderName));
    }

    private static string ResolveCacheBaseRoot(string runtimeRoot)
    {
        string normalized = runtimeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(normalized);
        if (!leaf.Equals("Runtime", StringComparison.OrdinalIgnoreCase))
        {
            return runtimeRoot;
        }

        string? parent = Path.GetDirectoryName(normalized);
        return string.IsNullOrWhiteSpace(parent)
            ? runtimeRoot
            : parent;
    }

    private static int CalculateStepProgressPercent(int stepIndex, int stepCount)
    {
        if (stepCount <= 0)
        {
            return 0;
        }

        return (int)Math.Round((double)stepIndex / stepCount * 100d);
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        int unit = 0;
        while (size >= 1024d && unit < units.Length - 1)
        {
            size /= 1024d;
            unit++;
        }

        return unit == 0
            ? $"{size:F0} {units[unit]}"
            : $"{size:F1} {units[unit]}";
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) ||
            !Directory.Exists(sourceDirectory) ||
            sourceDirectory.Equals(destinationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourceFilePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourceFilePath);
            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            string? destinationFolder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            File.Copy(sourceFilePath, destinationPath, overwrite: true);
        }
    }

    private sealed class DelegateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public DelegateProgress(Action<T> callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Report(T value)
        {
            _callback(value);
        }
    }
}
