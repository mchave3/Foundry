// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Deployment.Steps;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Operations;

namespace Foundry.Deploy.Tests;

public sealed class ConfigureWindowsOptionalFeaturesStepTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotCallServicingService()
    {
        using var workspace = new TestWorkspace();
        var service = new RecordingWindowsDeploymentService();
        DeploymentStepExecutionContext context = CreateContext(workspace, service, new DeployWindowsOptionalFeatureSettings());

        DeploymentStepResult result = await ExecuteAsync(service, context);

        Assert.Equal(DeploymentStepState.Succeeded, result.State);
        Assert.Equal(0, service.ConfigureOptionalFeaturesCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDryRun_ReportsAggregateCountsWithoutCallingServicingService()
    {
        using var workspace = new TestWorkspace();
        var service = new RecordingWindowsDeploymentService();
        DeployWindowsOptionalFeatureSettings settings = Settings(
            Action("NetFx3", enable: true),
            Action("TelnetClient", enable: false));
        DeploymentStepExecutionContext context = CreateContext(workspace, service, settings, isDryRun: true);

        DeploymentStepResult result = await ExecuteAsync(service, context);

        Assert.Equal(DeploymentStepState.Succeeded, result.State);
        Assert.Equal(0, service.ConfigureOptionalFeaturesCallCount);
        Assert.Contains("2", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetWindowsIsMissing_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var service = new RecordingWindowsDeploymentService();
        DeploymentStepExecutionContext context = CreateContext(workspace, service, Settings(Action("TelnetClient", false)));
        context.RuntimeState.TargetWindowsPartitionRoot = null;

        DeploymentStepResult result = await ExecuteAsync(service, context);

        Assert.Equal(DeploymentStepState.Failed, result.State);
        Assert.Equal(0, service.ConfigureOptionalFeaturesCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOperatingSystemImageIsMissing_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var service = new RecordingWindowsDeploymentService();
        DeploymentStepExecutionContext context = CreateContext(workspace, service, Settings(Action("NetFx3", true)));
        context.RuntimeState.DownloadedOperatingSystemPath = Path.Combine(workspace.Root, "missing.esd");

        DeploymentStepResult result = await ExecuteAsync(service, context);

        Assert.Equal(DeploymentStepState.Failed, result.State);
        Assert.Equal(0, service.ConfigureOptionalFeaturesCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAppliedImageIndexIsMissing_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var service = new RecordingWindowsDeploymentService();
        DeploymentStepExecutionContext context = CreateContext(workspace, service, Settings(Action("NetFx3", true)));
        context.RuntimeState.AppliedImageIndex = null;

        DeploymentStepResult result = await ExecuteAsync(service, context);

        Assert.Equal(DeploymentStepState.Failed, result.State);
        Assert.Contains("image index", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, service.ConfigureOptionalFeaturesCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConfigurationIsValid_ServicesFeaturesWithTargetWorkspacePaths()
    {
        using var workspace = new TestWorkspace();
        var service = new RecordingWindowsDeploymentService
        {
            Result = new WindowsOptionalFeatureServicingResult
            {
                RequestedActionCount = 2,
                ChangedActionCount = 1,
                AlreadySatisfiedActionCount = 1,
                MatchingSourceUsed = true
            }
        };
        DeployWindowsOptionalFeatureSettings settings = Settings(
            Action("NetFx3", enable: true),
            Action("TelnetClient", enable: false));
        DeploymentStepExecutionContext context = CreateContext(workspace, service, settings);

        DeploymentStepResult result = await ExecuteAsync(service, context);

        Assert.Equal(DeploymentStepState.Succeeded, result.State);
        Assert.Equal(1, service.ConfigureOptionalFeaturesCallCount);
        Assert.Equal(Path.Combine(workspace.TargetFoundryRoot, "Temp", "Dism", "OptionalFeatures"), service.ScratchDirectory);
        Assert.Equal(Path.Combine(workspace.TargetFoundryRoot, "Temp", "WindowsSetupMedia"), service.SourceExtractionDirectory);
        Assert.Equal(Path.Combine(workspace.TargetFoundryRoot, "Temp", "Deployment"), service.WorkingDirectory);
        Assert.Equal(9, service.AppliedImageIndex);
        Assert.Equal(2, service.Settings!.Actions.Count);
        Assert.Equal(DeploymentOperationNames.ConfigureWindowsOptionalFeatures, context.RuntimeState.CurrentOperation);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConfigurationIsInvalid_ReturnsStructuredValidationFailure()
    {
        using var workspace = new TestWorkspace();
        var service = new RecordingWindowsDeploymentService();
        DeployWindowsOptionalFeatureAction action = Action("TelnetClient", enable: true);
        DeploymentStepExecutionContext context = CreateContext(workspace, service, Settings(action, action));

        DeploymentStepResult result = await ExecuteAsync(service, context);

        Assert.Equal(DeploymentStepState.Failed, result.State);
        Assert.Equal(DeploymentOperationNames.ValidateWindowsOptionalFeatures, result.Failure?.OperationName);
        Assert.Equal(DeploymentFailureReasons.InvalidInput, result.Failure?.Reason);
        Assert.Equal(0, service.ConfigureOptionalFeaturesCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenServicingFails_PropagatesFailure()
    {
        using var workspace = new TestWorkspace();
        var service = new RecordingWindowsDeploymentService
        {
            Exception = new InvalidOperationException("DISM failed")
        };
        DeploymentStepExecutionContext context = CreateContext(workspace, service, Settings(Action("TelnetClient", false)));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(service, context));

        Assert.Equal("DISM failed", exception.Message);
    }

    private static async Task<DeploymentStepResult> ExecuteAsync(
        RecordingWindowsDeploymentService service,
        DeploymentStepExecutionContext context)
    {
        var step = new ConfigureWindowsOptionalFeaturesStep(service);
        context.SetCurrentStep(step, 1);
        return await step.ExecuteAsync(context, TestContext.Current.CancellationToken);
    }

    private static DeploymentStepExecutionContext CreateContext(
        TestWorkspace workspace,
        RecordingWindowsDeploymentService service,
        DeployWindowsOptionalFeatureSettings settings,
        bool isDryRun = false)
    {
        File.WriteAllText(workspace.ImagePath, "test");
        var operatingSystem = new OperatingSystemCatalogItem
        {
            Architecture = "x64",
            Edition = "Windows 11 Pro",
            BuildMajor = 26200
        };
        var request = new DeploymentContext
        {
            Mode = DeploymentMode.Iso,
            CacheRootPath = workspace.Root,
            TargetDiskNumber = 1,
            TargetComputerName = "LAB01",
            OperatingSystem = operatingSystem,
            DriverPackSelectionKind = DriverPackSelectionKind.None,
            WindowsOptionalFeatures = settings,
            IsDryRun = isDryRun
        };
        var runtime = new DeploymentRuntimeState
        {
            WorkspaceRoot = workspace.Root,
            Mode = DeploymentMode.Iso,
            TargetWindowsPartitionRoot = workspace.WindowsRoot,
            TargetFoundryRoot = workspace.TargetFoundryRoot,
            DownloadedOperatingSystemPath = workspace.ImagePath,
            AppliedImageIndex = 9,
            WindowsOptionalFeatures = settings
        };

        return new DeploymentStepExecutionContext(
            request,
            runtime,
            [DeploymentStepNames.ConfigureWindowsOptionalFeatures],
            new FakeOperationProgressService(),
            new FakeDeploymentLogService(),
            new FakeTargetDiskService(),
            _ => { });
    }

    private static DeployWindowsOptionalFeatureSettings Settings(params DeployWindowsOptionalFeatureAction[] actions)
        => new() { IsEnabled = true, Actions = actions };

    private static DeployWindowsOptionalFeatureAction Action(string featureName, bool enable)
        => new()
        {
            Id = WindowsOptionalFeatureCatalog.Entries.Single(
                entry => string.Equals(entry.FeatureName, featureName, StringComparison.OrdinalIgnoreCase)).Id,
            Enable = enable
        };

    private sealed class RecordingWindowsDeploymentService : IWindowsDeploymentService
    {
        public int ConfigureOptionalFeaturesCallCount { get; private set; }
        public DeployWindowsOptionalFeatureSettings? Settings { get; private set; }
        public int? AppliedImageIndex { get; private set; }
        public string? ScratchDirectory { get; private set; }
        public string? SourceExtractionDirectory { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public WindowsOptionalFeatureServicingResult Result { get; init; } = new();
        public Exception? Exception { get; init; }

        public Task<WindowsOptionalFeatureServicingResult> ConfigureOfflineWindowsOptionalFeaturesAsync(
            string setupMediaImagePath,
            string windowsPartitionRoot,
            int appliedImageIndex,
            DeployWindowsOptionalFeatureSettings settings,
            string scratchDirectory,
            string sourceExtractionDirectory,
            string workingDirectory,
            CancellationToken cancellationToken = default,
            IProgress<double>? progress = null,
            Action? onInspectionStarted = null,
            Action? onSourcePreparationStarted = null,
            Action? onServicingStarted = null)
        {
            ConfigureOptionalFeaturesCallCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            Settings = settings;
            AppliedImageIndex = appliedImageIndex;
            ScratchDirectory = scratchDirectory;
            SourceExtractionDirectory = sourceExtractionDirectory;
            WorkingDirectory = workingDirectory;
            onInspectionStarted?.Invoke();
            onSourcePreparationStarted?.Invoke();
            onServicingStarted?.Invoke();
            progress?.Report(100);
            return Task.FromResult(Result);
        }

        public Task<DeploymentTargetLayout> PrepareTargetDiskAsync(TargetDiskIdentity expectedDisk, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ResolveImageIndexAsync(string imagePath, string requestedEdition, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApplyImageAsync(string imagePath, int imageIndex, string windowsPartitionRoot, string scratchDirectory, string workingDirectory, CancellationToken cancellationToken = default, IProgress<double>? progress = null) => throw new NotSupportedException();
        public Task<string?> GetAppliedWindowsEditionAsync(string windowsPartitionRoot, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfigureOfflineComputerNameAsync(string windowsPartitionRoot, string computerName, string processorArchitecture, string? defaultTimeZoneId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfigureOfflineOobeAsync(string windowsPartitionRoot, DeployOobeSettings settings, string processorArchitecture, string workingDirectory, string workspaceRootPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfigureOfflineAiComponentRemovalAsync(string windowsPartitionRoot, DeployAiComponentRemovalSettings settings, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfigureRecoveryEnvironmentAsync(string windowsPartitionRoot, string recoveryPartitionRoot, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SealRecoveryPartitionAsync(string recoveryPartitionRoot, char recoveryPartitionLetter, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApplyOfflineDriversAsync(string windowsPartitionRoot, string driverRoot, string scratchDirectory, string workingDirectory, CancellationToken cancellationToken = default, IProgress<double>? progress = null) => throw new NotSupportedException();
        public Task ApplyRecoveryDriversAsync(string recoveryPartitionRoot, string driverRoot, string scratchDirectory, string workingDirectory, CancellationToken cancellationToken = default, IProgress<double>? mountProgress = null, IProgress<double>? applyProgress = null, IProgress<double>? unmountProgress = null, Action? onMountStarted = null, Action? onApplyStarted = null, Action? onUnmountStarted = null) => throw new NotSupportedException();
        public Task ConfigureBootAsync(string windowsPartitionRoot, string systemPartitionRoot, int operatingSystemBuildMajor, string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeDeploymentLogService : IDeploymentLogService
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
                StateFilePath = Path.Combine(stateDirectory, "state.json")
            };
        }

        public Task AppendAsync(DeploymentLogSession session, DeploymentLogLevel level, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveStateAsync<TState>(DeploymentLogSession session, TState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeOperationProgressService : IOperationProgressService
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

    private sealed class FakeTargetDiskService : ITargetDiskService
    {
        public Task<IReadOnlyList<TargetDiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TargetDiskInfo>>([]);

        public Task<int?> GetDiskNumberForPathAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "Foundry.Deploy.Tests", Guid.NewGuid().ToString("N"));
            WindowsRoot = Path.Combine(Root, "WindowsPartition");
            TargetFoundryRoot = Path.Combine(WindowsRoot, "Foundry");
            ImagePath = Path.Combine(Root, "install.esd");
            Directory.CreateDirectory(TargetFoundryRoot);
        }

        public string Root { get; }
        public string WindowsRoot { get; }
        public string TargetFoundryRoot { get; }
        public string ImagePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
