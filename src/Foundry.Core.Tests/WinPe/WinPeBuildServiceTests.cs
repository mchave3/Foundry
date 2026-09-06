// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeBuildServiceTests
{
    [Fact]
    public async Task BuildAsync_RejectsUnsupportedBatchPathBeforeDeletingTheWorkspace()
    {
        using TempWinPeBuildWorkspace workspace = TempWinPeBuildWorkspace.Create();
        string workingDirectory = Path.Combine(workspace.OutputDirectoryPath, "unsafe%path");
        Directory.CreateDirectory(workingDirectory);
        string sentinel = Path.Combine(workingDirectory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "original", TestContext.Current.CancellationToken);
        var service = new WinPeBuildService(new WinPeToolResolver(() => workspace.KitsRootPath), new FakeBuildRunner());

        WinPeResult<WinPeBuildArtifact> result = await service.BuildAsync(new WinPeBuildOptions
        {
            OutputDirectoryPath = workspace.OutputDirectoryPath,
            WorkingDirectoryPath = workingDirectory,
            CleanExistingWorkingDirectory = true,
            Architecture = WinPeArchitecture.X64
        }, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("original", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildAsync_WhenOptionsAreNull_ReturnsValidationFailure()
    {
        var service = new WinPeBuildService();

        WinPeResult<WinPeBuildArtifact> result = await service.BuildAsync(null!);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.ValidationFailed, result.Error?.Code);
    }

    [Fact]
    public async Task BuildAsync_WhenOutputDirectoryIsMissing_ReturnsValidationFailure()
    {
        var service = new WinPeBuildService();

        WinPeResult<WinPeBuildArtifact> result = await service.BuildAsync(new WinPeBuildOptions());

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.ValidationFailed, result.Error?.Code);
    }

    [Fact]
    public async Task BuildAsync_WhenArchitectureIsInvalid_ReturnsValidationFailure()
    {
        var service = new WinPeBuildService();

        WinPeResult<WinPeBuildArtifact> result = await service.BuildAsync(new WinPeBuildOptions
        {
            OutputDirectoryPath = "C:\\Temp",
            Architecture = (WinPeArchitecture)999
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.ValidationFailed, result.Error?.Code);
    }

    [Fact]
    public async Task BuildAsync_WhenCopypeSucceeds_CreatesExpectedWorkspaceFolders()
    {
        using TempWinPeBuildWorkspace workspace = TempWinPeBuildWorkspace.Create();
        var runner = new FakeBuildRunner();
        var service = new WinPeBuildService(
            new WinPeToolResolver(() => workspace.KitsRootPath),
            runner);

        WinPeResult<WinPeBuildArtifact> result = await service.BuildAsync(
            new WinPeBuildOptions
            {
                OutputDirectoryPath = workspace.OutputDirectoryPath,
                Architecture = WinPeArchitecture.X64
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.True(Directory.Exists(Path.Combine(result.Value!.WorkingDirectoryPath, "media")));
        Assert.True(Directory.Exists(Path.Combine(result.Value.WorkingDirectoryPath, "mount")));
        Assert.True(Directory.Exists(Path.Combine(result.Value.WorkingDirectoryPath, "drivers")));
        Assert.True(Directory.Exists(Path.Combine(result.Value.WorkingDirectoryPath, "logs")));
        Assert.True(Directory.Exists(Path.Combine(result.Value.WorkingDirectoryPath, "temp")));
    }

    [Fact]
    public async Task BuildAsync_WhenCopypeFails_ReturnsStructuredProcessDiagnostic()
    {
        using TempWinPeBuildWorkspace workspace = TempWinPeBuildWorkspace.Create();
        var service = new WinPeBuildService(
            new WinPeToolResolver(() => workspace.KitsRootPath),
            new FakeBuildRunner(exitCode: 9));

        WinPeResult<WinPeBuildArtifact> result = await service.BuildAsync(
            new WinPeBuildOptions
            {
                OutputDirectoryPath = workspace.OutputDirectoryPath,
                Architecture = WinPeArchitecture.X64
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeFailureKinds.Process, result.Error?.FailureKind);
        Assert.Equal(WinPeFailureReasons.NonZeroExit, result.Error?.FailureReason);
        Assert.Equal("copype", result.Error?.ToolName);
        Assert.Equal(9, result.Error?.ExitCode);
    }

    [Fact]
    public async Task BuildAsync_WhenCopypeTimesOut_PreservesProcessTimeoutAndTerminationMetadata()
    {
        using TempWinPeBuildWorkspace workspace = TempWinPeBuildWorkspace.Create();
        var exception = new TimeoutException("The native operation exceeded its deadline.");
        exception.Data["ProcessRootExitConfirmed"] = true;
        exception.Data["ProcessTreeTerminationConfirmed"] = false;
        var service = new WinPeBuildService(
            new WinPeToolResolver(() => workspace.KitsRootPath),
            new FakeBuildRunner(exception: exception));

        WinPeResult<WinPeBuildArtifact> result = await service.BuildAsync(
            new WinPeBuildOptions
            {
                OutputDirectoryPath = workspace.OutputDirectoryPath,
                Architecture = WinPeArchitecture.X64
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeFailureKinds.Process, result.Error?.FailureKind);
        Assert.Equal(WinPeFailureReasons.Timeout, result.Error?.FailureReason);
        Assert.Equal("copype", result.Error?.ToolName);
        Assert.Same(exception, result.Error?.Exception);
        Assert.Equal(true, result.Error?.Exception?.Data["ProcessRootExitConfirmed"]);
        Assert.Equal(false, result.Error?.Exception?.Data["ProcessTreeTerminationConfirmed"]);
    }

    private sealed class FakeBuildRunner(int exitCode = 0, Exception? exception = null) : IWinPeProcessRunner
    {
        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException("Executable calls must pass argument tokens.");
        }

        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            IReadOnlyList<string> argumentList,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            string arguments = string.Join(' ', argumentList);
            return Task.FromResult(new WinPeProcessExecution
            {
                ExitCode = 0,
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory
            });
        }

        public Task<WinPeProcessExecution> RunCmdScriptAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            if (exception is not null)
            {
                throw exception;
            }

            if (exitCode == 0)
            {
                string workingRoot = scriptArguments[(scriptArguments.IndexOf('"') + 1)..^1];
                string bootWimPath = Path.Combine(workingRoot, "media", "sources", "boot.wim");
                Directory.CreateDirectory(Path.GetDirectoryName(bootWimPath)!);
                File.WriteAllText(bootWimPath, "boot");
            }

            return Task.FromResult(new WinPeProcessExecution
            {
                ExitCode = exitCode,
                FileName = scriptPath,
                Arguments = scriptArguments,
                WorkingDirectory = workingDirectory,
                StandardError = exitCode == 0 ? string.Empty : "copype failed"
            });
        }

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            return RunCmdScriptAsync(scriptPath, scriptArguments, workingDirectory, cancellationToken);
        }
    }

    private sealed class TempWinPeBuildWorkspace : IDisposable
    {
        private TempWinPeBuildWorkspace(string rootPath)
        {
            RootPath = rootPath;
            OutputDirectoryPath = Path.Combine(rootPath, "Workspaces", "WinPe");
            KitsRootPath = Path.Combine(rootPath, "Kits");

            string winPeRoot = Path.Combine(
                KitsRootPath,
                "Assessment and Deployment Kit",
                "Windows Preinstallation Environment");
            Directory.CreateDirectory(winPeRoot);
            File.WriteAllText(Path.Combine(winPeRoot, "copype.cmd"), "copype");
            File.WriteAllText(Path.Combine(winPeRoot, "MakeWinPEMedia.cmd"), "makewinpemedia");
        }

        public string RootPath { get; }
        public string OutputDirectoryPath { get; }
        public string KitsRootPath { get; }

        public static TempWinPeBuildWorkspace Create()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"foundry-winpe-build-{Guid.NewGuid():N}");
            return new TempWinPeBuildWorkspace(rootPath);
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
