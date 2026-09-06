// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Operations;

namespace Foundry.Deploy.Tests;

internal sealed class DriverApplicationStepTestFixture : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "Foundry.Deploy.Tests",
        Guid.NewGuid().ToString("N"));

    public DriverApplicationStepTestFixture()
    {
        WorkspaceRoot = Path.Combine(_rootPath, "Workspace");
        WindowsRoot = Path.Combine(_rootPath, "Windows");
        RecoveryRoot = Path.Combine(_rootPath, "Recovery");
        DriverRoot = Path.Combine(_rootPath, "Drivers");

        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(WindowsRoot);
        Directory.CreateDirectory(RecoveryRoot);
        Directory.CreateDirectory(DriverRoot);
        File.WriteAllText(Path.Combine(DriverRoot, "driver.inf"), "[Version]");
    }

    public RecordingDriverApplicationService DeploymentService { get; } = new();

    public string WorkspaceRoot { get; }

    public string WindowsRoot { get; }

    public string RecoveryRoot { get; }

    public string DriverRoot { get; }

    public string CreateDriverPackage()
    {
        string packagePath = Path.Combine(_rootPath, "driver.exe");
        File.WriteAllBytes(packagePath, [1, 2, 3]);
        return packagePath;
    }

    public DeploymentStepExecutionContext CreateContext(bool isDryRun = false)
    {
        var request = new DeploymentContext
        {
            Mode = DeploymentMode.Iso,
            IsDryRun = isDryRun,
            CacheRootPath = WorkspaceRoot,
            TargetDiskNumber = 1,
            TargetComputerName = "LAB01",
            OperatingSystem = new OperatingSystemCatalogItem(),
            DriverPackSelectionKind = DriverPackSelectionKind.OemCatalog
        };
        var runtimeState = new DeploymentRuntimeState
        {
            WorkspaceRoot = WorkspaceRoot,
            TargetWindowsPartitionRoot = WindowsRoot,
            TargetRecoveryPartitionRoot = RecoveryRoot,
            TargetFoundryRoot = Path.Combine(WindowsRoot, "Foundry"),
            ExtractedDriverPackPath = DriverRoot,
            DriverPackInstallMode = DriverPackInstallMode.OfflineInf,
            WinReConfigured = true
        };

        return new DeploymentStepExecutionContext(
            request,
            runtimeState,
            [],
            new DriverApplicationOperationProgressService(),
            new DriverApplicationLogService(),
            new DriverApplicationTargetDiskService(),
            _ => { });
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

internal sealed class RecordingDriverApplicationService : IWindowsDeploymentService
{
    public int WindowsApplyCount { get; private set; }

    public int RecoveryApplyCount { get; private set; }

    public Task ApplyOfflineDriversAsync(string windowsPartitionRoot, string driverRoot, string scratchDirectory, string workingDirectory, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        WindowsApplyCount++;
        return Task.CompletedTask;
    }

    public Task ApplyRecoveryDriversAsync(string recoveryPartitionRoot, string driverRoot, string scratchDirectory, string workingDirectory, CancellationToken cancellationToken = default, IProgress<double>? mountProgress = null, IProgress<double>? applyProgress = null, IProgress<double>? unmountProgress = null, Action? onMountStarted = null, Action? onApplyStarted = null, Action? onUnmountStarted = null)
    {
        RecoveryApplyCount++;
        return Task.CompletedTask;
    }

    public Task<DeploymentTargetLayout> PrepareTargetDiskAsync(TargetDiskIdentity expectedDisk, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> ResolveImageIndexAsync(string imagePath, string requestedEdition, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ApplyImageAsync(string imagePath, int imageIndex, string windowsPartitionRoot, string scratchDirectory, string workingDirectory, CancellationToken cancellationToken = default, IProgress<double>? progress = null) => throw new NotSupportedException();

    public Task<string?> GetAppliedWindowsEditionAsync(string windowsPartitionRoot, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ConfigureBootAsync(string windowsPartitionRoot, string systemPartitionRoot, int operatingSystemBuildMajor, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ConfigureOfflineComputerNameAsync(string windowsPartitionRoot, string computerName, string processorArchitecture, string? defaultTimeZoneId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ConfigureOfflineOobeAsync(string windowsPartitionRoot, DeployOobeSettings settings, string processorArchitecture, string workingDirectory, string workspaceRootPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ConfigureOfflineAiComponentRemovalAsync(string windowsPartitionRoot, DeployAiComponentRemovalSettings settings, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<WindowsOptionalFeatureServicingResult> ConfigureOfflineWindowsOptionalFeaturesAsync(string setupMediaImagePath, string windowsPartitionRoot, int appliedImageIndex, DeployWindowsOptionalFeatureSettings settings, string scratchDirectory, string sourceExtractionDirectory, string workingDirectory, CancellationToken cancellationToken = default, IProgress<double>? progress = null, Action? onInspectionStarted = null, Action? onSourcePreparationStarted = null, Action? onServicingStarted = null) => throw new NotSupportedException();

    public Task ConfigureRecoveryEnvironmentAsync(string windowsPartitionRoot, string recoveryPartitionRoot, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SealRecoveryPartitionAsync(string recoveryPartitionRoot, char recoveryPartitionLetter, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class DriverApplicationLogService : IDeploymentLogService
{
    public DeploymentLogSession Initialize(string rootPath)
    {
        string logsDirectory = Path.Combine(rootPath, "Logs");
        string stateDirectory = Path.Combine(rootPath, "State");
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(stateDirectory);

        return new DeploymentLogSession
        {
            RootPath = rootPath,
            LogsDirectoryPath = logsDirectory,
            StateDirectoryPath = stateDirectory,
            StateFilePath = Path.Combine(stateDirectory, "deployment-state.json")
        };
    }

    public Task AppendAsync(DeploymentLogSession session, DeploymentLogLevel level, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveStateAsync<TState>(DeploymentLogSession session, TState state, CancellationToken cancellationToken = default) => Task.CompletedTask;

}

internal sealed class DriverApplicationOperationProgressService : IOperationProgressService
{
    public bool IsOperationInProgress => false;
    public int Progress => 0;
    public string? Status => null;
    public OperationKind? CurrentOperation => null;
    public bool CanStartOperation => true;
    public event EventHandler? ProgressChanged;
    public bool TryStart(OperationKind kind, string initialStatus, int initialProgress = 0) => true;
    public void Report(int progress, string? status = null) => ProgressChanged?.Invoke(this, EventArgs.Empty);
    public void Complete(string? status = null) => ProgressChanged?.Invoke(this, EventArgs.Empty);
    public void Fail(string status) => ProgressChanged?.Invoke(this, EventArgs.Empty);
    public void ResetToIdle() => ProgressChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class DriverApplicationTargetDiskService : ITargetDiskService
{
    public Task<IReadOnlyList<TargetDiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetDiskInfo>>([]);

    public Task<int?> GetDiskNumberForPathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
}
