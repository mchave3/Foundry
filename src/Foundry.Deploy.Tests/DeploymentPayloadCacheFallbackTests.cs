// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Deployment.Steps;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Operations;

namespace Foundry.Deploy.Tests;

public sealed class DeploymentPayloadCacheFallbackTests
{
    [Fact]
    public async Task DownloadDriverPackStep_CatalogCompletionLogOmitsDownloadQuery()
    {
        using TempDeploymentWorkspace workspace = TempDeploymentWorkspace.Create();
        var logs = new FakeDeploymentLogService();
        using DeploymentStepExecutionContext context = CreateExecutionContext(workspace,
            selectionKind: DriverPackSelectionKind.MicrosoftUpdateCatalog, logService: logs);
        var downloadedDriver = new MicrosoftUpdateCatalogDownloadedDriver
        {
            UpdateId = "update-1",
            Title = "Network driver",
            Version = "1.0",
            Size = "1 MB",
            DownloadUrl = "https://download.windowsupdate.com/driver.cab?token=PRIVATE_QUERY_SENTINEL"
        };
        var step = new DownloadDriverPackStep(new FakeMicrosoftUpdateCatalogDriverService(downloadedDriver), new CapturingArtifactDownloadService());

        DeploymentStepResult result = await step.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentStepState.Succeeded, result.State);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("PRIVATE_QUERY_SENTINEL", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("?token=", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, message => message.Contains("SourceHost=download.windowsupdate.com", StringComparison.Ordinal) &&
            message.Contains("UpdateId=update-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadOperatingSystemImageStep_WhenUsbCacheHasInsufficientSpace_UsesTargetCache()
    {
        using TempDeploymentWorkspace workspace = TempDeploymentWorkspace.Create();
        var downloadService = new CapturingArtifactDownloadService();
        DeploymentStepExecutionContext context = CreateExecutionContext(
            workspace,
            operatingSystemSizeBytes: 100);
        var step = new DownloadOperatingSystemImageStep(downloadService, new PayloadCachePlacementService(downloadService, new TestStorageProbe()));

        DeploymentStepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(DeploymentStepState.Succeeded, result.State);
        Assert.Equal(
            Path.Combine(workspace.TargetFoundryRoot, "Cache", "OperatingSystems", downloadService.Artifact!.CacheKey, "install.wim"),
            downloadService.DestinationPath);
    }

    [Fact]
    public async Task DownloadOperatingSystemImageStep_WhenUsbCacheHasEnoughSpace_UsesUsbCache()
    {
        using TempDeploymentWorkspace workspace = TempDeploymentWorkspace.Create();
        var downloadService = new CapturingArtifactDownloadService();
        DeploymentStepExecutionContext context = CreateExecutionContext(
            workspace,
            operatingSystemSizeBytes: 1);
        var step = new DownloadOperatingSystemImageStep(downloadService, new PayloadCachePlacementService(downloadService, new TestStorageProbe()));

        DeploymentStepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(DeploymentStepState.Succeeded, result.State);
        Assert.Equal(
            Path.Combine(workspace.UsbCacheRoot, "Cache", "OperatingSystems", downloadService.Artifact!.CacheKey, "install.wim"),
            downloadService.DestinationPath);
    }

    [Fact]
    public async Task DownloadDriverPackStep_WhenUsbCacheHasInsufficientSpace_UsesTargetCache()
    {
        using TempDeploymentWorkspace workspace = TempDeploymentWorkspace.Create();
        var downloadService = new CapturingArtifactDownloadService();
        DeploymentStepExecutionContext context = CreateExecutionContext(
            workspace,
            driverPackSizeBytes: 100);
        var step = new DownloadDriverPackStep(new FakeMicrosoftUpdateCatalogDriverService(), downloadService, new PayloadCachePlacementService(downloadService, new TestStorageProbe()));

        DeploymentStepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(DeploymentStepState.Succeeded, result.State);
        Assert.Equal(
            Path.Combine(workspace.TargetFoundryRoot, "Cache", "DriverPacks", "Contoso", downloadService.Artifact!.CacheKey, "drivers.cab"),
            downloadService.DestinationPath);
    }

    [Fact]
    public async Task DownloadDriverPackStep_WhenUsbCacheHasEnoughSpace_UsesUsbCache()
    {
        using TempDeploymentWorkspace workspace = TempDeploymentWorkspace.Create();
        var downloadService = new CapturingArtifactDownloadService();
        DeploymentStepExecutionContext context = CreateExecutionContext(
            workspace,
            driverPackSizeBytes: 1);
        var step = new DownloadDriverPackStep(new FakeMicrosoftUpdateCatalogDriverService(), downloadService, new PayloadCachePlacementService(downloadService, new TestStorageProbe()));

        DeploymentStepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(DeploymentStepState.Succeeded, result.State);
        Assert.Equal(
            Path.Combine(workspace.UsbCacheRoot, "Cache", "DriverPacks", "Contoso", downloadService.Artifact!.CacheKey, "drivers.cab"),
            downloadService.DestinationPath);
    }

    private static DeploymentStepExecutionContext CreateExecutionContext(
        TempDeploymentWorkspace workspace,
        long operatingSystemSizeBytes = 1,
        long driverPackSizeBytes = 1,
        DriverPackSelectionKind selectionKind = DriverPackSelectionKind.OemCatalog,
        IDeploymentLogService? logService = null)
    {
        var request = new DeploymentContext
        {
            Mode = DeploymentMode.Usb,
            CacheRootPath = workspace.UsbRuntimeRoot,
            TargetDiskNumber = 1,
            TargetComputerName = "LAB01",
            OperatingSystem = new OperatingSystemCatalogItem
            {
                CatalogRevision = "trusted-revision",
                SourceId = "os-1",
                Sha256 = new string('A', 64),
                FileName = "install.wim",
                Url = "https://example.test/install.wim",
                SizeBytes = operatingSystemSizeBytes
            },
            DriverPackSelectionKind = selectionKind,
            DriverPack = new DriverPackCatalogItem
            {
                CatalogRevision = "trusted-revision",
                Id = "driver-1",
                Sha256 = new string('B', 64),
                Manufacturer = "Contoso",
                Name = "Contoso Driver Pack",
                FileName = "drivers.cab",
                DownloadUrl = "https://example.test/drivers.cab",
                SizeBytes = driverPackSizeBytes
            },
            IsDryRun = false
        };

        var runtimeState = new DeploymentRuntimeState
        {
            WorkspaceRoot = workspace.WorkspaceRoot,
            HardwareProfile = new HardwareProfile(),
            Mode = DeploymentMode.Usb,
            TargetFoundryRoot = workspace.TargetFoundryRoot,
            ResolvedCache = new CacheResolution
            {
                RootPath = workspace.UsbRuntimeRoot,
                Source = "test"
            }
        };

        return new DeploymentStepExecutionContext(
            request,
            runtimeState,
            [],
            new FakeOperationProgressService(),
            logService ?? new FakeDeploymentLogService(),
            new FakeTargetDiskService(),
            _ => { });
    }

    private sealed class CapturingArtifactDownloadService : IArtifactDownloadService
    {
        public string? DestinationPath { get; private set; }

        public ArtifactIdentity? Artifact { get; private set; }
        public Task<ArtifactDownloadResult?> TryUseCachedAsync(ArtifactIdentity artifact, string path, CancellationToken cancellationToken = default) => Task.FromResult<ArtifactDownloadResult?>(null);
        public Task<ArtifactDownloadResult> DownloadAsync(
            ArtifactIdentity artifact,
            string destinationPath,
            CancellationToken cancellationToken = default,
            IProgress<DownloadProgress>? progress = null)
        {
            Artifact = artifact;
            DestinationPath = destinationPath;
            return Task.FromResult(new ArtifactDownloadResult
            {
                DestinationPath = destinationPath,
                Downloaded = true,
                Method = "test",
                SizeBytes = 1
            });
        }
    }

    private sealed class TestStorageProbe : IVolumeStorageProbe
    {
        public VolumeStorageStatus Inspect(string directory) => new(true, true, directory.Contains("TargetWindows", StringComparison.Ordinal) ? 1000 : 10);
    }

    private sealed class FakeMicrosoftUpdateCatalogDriverService(MicrosoftUpdateCatalogDownloadedDriver? downloadedDriver = null) : IMicrosoftUpdateCatalogDriverService
    {
        public Task<MicrosoftUpdateCatalogDriverResult> DownloadAsync(
            HardwareProfile hardwareProfile,
            OperatingSystemCatalogItem operatingSystem,
            string destinationDirectory,
            string cacheDirectory,
            CancellationToken cancellationToken = default,
            IProgress<double>? progress = null)
        {
            return Task.FromResult(new MicrosoftUpdateCatalogDriverResult
            {
                DestinationDirectory = destinationDirectory,
                IsPayloadAvailable = downloadedDriver is not null,
                DownloadedDrivers = downloadedDriver is null ? [] : [downloadedDriver],
                Message = "No Microsoft Update Catalog payload."
            });
        }

        public Task<MicrosoftUpdateCatalogDriverResult> ExpandAsync(
            string sourceDirectory,
            string destinationDirectory,
            CancellationToken cancellationToken = default,
            IProgress<double>? progress = null)
        {
            return Task.FromResult(new MicrosoftUpdateCatalogDriverResult
            {
                DestinationDirectory = destinationDirectory,
                IsPayloadAvailable = false,
                Message = "No Microsoft Update Catalog payload."
            });
        }
    }

    private sealed class TempDeploymentWorkspace : IDisposable
    {
        private TempDeploymentWorkspace(string rootPath)
        {
            WorkspaceRoot = Path.Combine(rootPath, "Workspace");
            UsbCacheRoot = Path.Combine(rootPath, "UsbCache");
            UsbRuntimeRoot = Path.Combine(UsbCacheRoot, "Runtime");
            TargetWindowsRoot = Path.Combine(rootPath, "TargetWindows");
            TargetFoundryRoot = Path.Combine(TargetWindowsRoot, "Foundry");

            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(UsbRuntimeRoot);
            Directory.CreateDirectory(TargetFoundryRoot);
        }

        public string WorkspaceRoot { get; }
        public string UsbCacheRoot { get; }
        public string UsbRuntimeRoot { get; }
        public string TargetWindowsRoot { get; }
        public string TargetFoundryRoot { get; }

        public static TempDeploymentWorkspace Create()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"foundry-deploy-cache-{Guid.NewGuid():N}");
            return new TempDeploymentWorkspace(rootPath);
        }

        public void Dispose()
        {
            string rootPath = Directory.GetParent(WorkspaceRoot)?.FullName
                ?? throw new InvalidOperationException("Unable to resolve test workspace root.");
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class FakeDeploymentLogService : IDeploymentLogService
    {
        public List<string> Messages { get; } = [];
        public DeploymentLogSession Initialize(string rootPath)
        {
            return new DeploymentLogSession
            {
                RootPath = rootPath,
                LogsDirectoryPath = Path.Combine(rootPath, "Logs"),
                StateDirectoryPath = Path.Combine(rootPath, "State"),
                StateFilePath = Path.Combine(rootPath, "State", "deployment-state.json")
            };
        }

        public Task AppendAsync(
            DeploymentLogSession session,
            DeploymentLogLevel level,
            string message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task SaveStateAsync<TState>(
            DeploymentLogSession session,
            TState state,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

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
        {
            return Task.FromResult<IReadOnlyList<TargetDiskInfo>>([]);
        }

        public Task<int?> GetDiskNumberForPathAsync(string path, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<int?>(null);
        }
    }
}
