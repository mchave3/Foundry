// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeIsoMediaServiceTests
{
    [Fact]
    public async Task CreateAsync_RejectsUnsupportedBatchPathBeforeReplacingExistingOutput()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: false);
        string outputPath = Path.Combine(temp.RootPath, "unsafe&name.iso");
        await File.WriteAllTextAsync(outputPath, "original", TestContext.Current.CancellationToken);
        var runner = new FakeIsoRunner();

        WinPeResult result = await new WinPeIsoMediaService(runner).CreateAsync(new WinPeIsoMediaOptions
        {
            PreparedWorkspace = temp.PreparedWorkspace,
            OutputIsoPath = outputPath,
            ForceOverwriteOutput = true,
            IsoTempDirectoryPath = Path.Combine(temp.RootPath, "iso-temp")
        }, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("original", await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        Assert.Empty(runner.Executions);
    }

    [Fact]
    public async Task CreateAsync_WhenBootExIsEnabled_PassesBootExToMakeWinPeMedia()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: true);
        string outputIsoPath = Path.Combine(temp.RootPath, "out", "foundry.iso");
        var runner = new FakeIsoRunner();
        var service = new WinPeIsoMediaService(runner);

        WinPeResult result = await service.CreateAsync(
            new WinPeIsoMediaOptions
            {
                PreparedWorkspace = temp.PreparedWorkspace,
                OutputIsoPath = outputIsoPath,
                IsoTempDirectoryPath = Path.Combine(temp.RootPath, "iso-temp")
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        WinPeProcessExecution execution = Assert.Single(runner.Executions);
        Assert.Contains("/ISO", execution.Arguments);
        Assert.Contains("/F", execution.Arguments);
        Assert.Contains("/bootex", execution.Arguments);
        Assert.True(File.Exists(outputIsoPath));
    }

    [Fact]
    public async Task CreateAsync_WhenOutputPathContainsNonAscii_UsesAsciiSafeOutputAndCopiesBack()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: false);
        string outputIsoPath = Path.Combine(temp.RootPath, "réseau", "foundry-é.iso");
        var runner = new FakeIsoRunner();
        var service = new WinPeIsoMediaService(runner);

        WinPeResult result = await service.CreateAsync(
            new WinPeIsoMediaOptions
            {
                PreparedWorkspace = temp.PreparedWorkspace,
                OutputIsoPath = outputIsoPath,
                IsoTempDirectoryPath = Path.Combine(temp.RootPath, "iso-temp")
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.True(File.Exists(outputIsoPath));
        Assert.DoesNotContain("réseau", runner.Executions[0].Arguments);
        Assert.DoesNotContain("foundry-é.iso", runner.Executions[0].Arguments);
    }

    [Fact]
    public async Task CreateAsync_WhenWorkspacePathContainsNonAscii_UsesAsciiSafeWorkspace()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: false, rootName: $"foundry-réseau-{Guid.NewGuid():N}");
        string outputIsoPath = Path.Combine(Path.GetTempPath(), $"foundry-output-{Guid.NewGuid():N}", "foundry.iso");
        var runner = new FakeIsoRunner();
        var service = new WinPeIsoMediaService(runner);

        try
        {
            WinPeResult result = await service.CreateAsync(
                new WinPeIsoMediaOptions
                {
                    PreparedWorkspace = temp.PreparedWorkspace,
                    OutputIsoPath = outputIsoPath,
                    IsoTempDirectoryPath = Path.Combine(Path.GetTempPath(), $"foundry-iso-temp-{Guid.NewGuid():N}")
                },
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Details);
            WinPeProcessExecution execution = Assert.Single(runner.Executions);
            Assert.DoesNotContain("réseau", execution.Arguments);
            Assert.DoesNotContain("réseau", execution.WorkingDirectory);
        }
        finally
        {
            if (File.Exists(outputIsoPath))
            {
                File.Delete(outputIsoPath);
            }

            string? outputDirectory = Path.GetDirectoryName(outputIsoPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CreateAsync_WhenProcessFails_ReturnsStructuredProcessDiagnostic()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: false);
        var runner = new FakeIsoRunner(new WinPeProcessExecution
        {
            ExitCode = 5,
            StandardError = "MakeWinPEMedia failed"
        });
        var service = new WinPeIsoMediaService(runner);

        WinPeResult result = await service.CreateAsync(
            new WinPeIsoMediaOptions
            {
                PreparedWorkspace = temp.PreparedWorkspace,
                OutputIsoPath = Path.Combine(temp.RootPath, "out", "foundry.iso"),
                IsoTempDirectoryPath = Path.Combine(temp.RootPath, "iso-temp")
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeFailureKinds.Process, result.Error!.FailureKind);
        Assert.Equal(WinPeFailureReasons.NonZeroExit, result.Error.FailureReason);
        Assert.Equal("MakeWinPEMedia", result.Error.ToolName);
        Assert.Equal(5, result.Error.ExitCode);
    }

    [Fact]
    public async Task CreateAsync_WhenProcessStartThrows_PreservesException()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: false);
        var exception = new InvalidOperationException("Could not start process.");
        var service = new WinPeIsoMediaService(new FakeIsoRunner(exception));

        WinPeResult result = await service.CreateAsync(
            new WinPeIsoMediaOptions
            {
                PreparedWorkspace = temp.PreparedWorkspace,
                OutputIsoPath = Path.Combine(temp.RootPath, "out", "foundry.iso"),
                IsoTempDirectoryPath = Path.Combine(temp.RootPath, "iso-temp")
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Same(exception, result.Error!.Exception);
        Assert.Equal(WinPeFailureReasons.ProcessStartFailed, result.Error.FailureReason);
    }

    [Fact]
    public async Task CreateAsync_WhenProcessSucceedsWithoutArtifact_ReportsArtifactMissing()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: false);
        var service = new WinPeIsoMediaService(new FakeIsoRunner(new WinPeProcessExecution()));

        WinPeResult result = await service.CreateAsync(
            new WinPeIsoMediaOptions
            {
                PreparedWorkspace = temp.PreparedWorkspace,
                OutputIsoPath = Path.Combine(temp.RootPath, "out", "foundry.iso"),
                IsoTempDirectoryPath = Path.Combine(temp.RootPath, "iso-temp")
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeFailureReasons.ArtifactMissing, result.Error?.FailureReason);
        Assert.Null(result.Error?.ExitCode);
    }

    [Fact]
    public async Task CreateAsync_WhenMakeWinPeMediaTimesOut_PreservesProcessTimeoutAndTerminationMetadata()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: false);
        var exception = new TimeoutException("The native operation exceeded its deadline.");
        exception.Data["ProcessRootExitConfirmed"] = true;
        exception.Data["ProcessTreeTerminationConfirmed"] = false;
        var service = new WinPeIsoMediaService(new FakeIsoRunner(exception));

        WinPeResult result = await service.CreateAsync(
            new WinPeIsoMediaOptions
            {
                PreparedWorkspace = temp.PreparedWorkspace,
                OutputIsoPath = Path.Combine(temp.RootPath, "out", "foundry.iso"),
                IsoTempDirectoryPath = Path.Combine(temp.RootPath, "iso-temp")
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeFailureKinds.Process, result.Error?.FailureKind);
        Assert.Equal(WinPeFailureReasons.Timeout, result.Error?.FailureReason);
        Assert.Equal("MakeWinPEMedia", result.Error?.ToolName);
        Assert.Same(exception, result.Error?.Exception);
        Assert.Equal(true, result.Error?.Exception?.Data["ProcessRootExitConfirmed"]);
        Assert.Equal(false, result.Error?.Exception?.Data["ProcessTreeTerminationConfirmed"]);
    }

    [Fact]
    public async Task CreateAsync_WhenFinalCopyFails_ReportsFileSystemFinalizationFailure()
    {
        using TempPreparedWorkspace temp = TempPreparedWorkspace.Create(useBootEx: false);
        string outputIsoPath = Path.Combine(temp.RootPath, "réseau", "blocked.iso");
        Directory.CreateDirectory(outputIsoPath);
        var service = new WinPeIsoMediaService(new FakeIsoRunner());

        WinPeResult result = await service.CreateAsync(
            new WinPeIsoMediaOptions
            {
                PreparedWorkspace = temp.PreparedWorkspace,
                OutputIsoPath = outputIsoPath,
                IsoTempDirectoryPath = Path.Combine(temp.RootPath, "iso-temp")
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("Finalize ISO output", result.Error?.Stage);
        Assert.Equal(WinPeFailureKinds.FileSystem, result.Error?.FailureKind);
        Assert.True(result.Error?.FailureReason is WinPeFailureReasons.IoError or WinPeFailureReasons.AccessDenied);
        Assert.Null(result.Error?.ToolName);
        Assert.NotNull(result.Error?.Exception);
    }

    private sealed class TempPreparedWorkspace : IDisposable
    {
        private TempPreparedWorkspace(string rootPath, WinPeWorkspacePreparationResult preparedWorkspace)
        {
            RootPath = rootPath;
            PreparedWorkspace = preparedWorkspace;
        }

        public string RootPath { get; }
        public WinPeWorkspacePreparationResult PreparedWorkspace { get; }

        public static TempPreparedWorkspace Create(bool useBootEx, string? rootName = null)
        {
            string root = Path.Combine(Path.GetTempPath(), rootName ?? $"foundry-iso-{Guid.NewGuid():N}");
            string media = Path.Combine(root, "work", "media");
            string bootWim = Path.Combine(media, "sources", "boot.wim");
            Directory.CreateDirectory(Path.GetDirectoryName(bootWim)!);
            File.WriteAllText(bootWim, "wim");

            var artifact = new WinPeBuildArtifact
            {
                WorkingDirectoryPath = Path.Combine(root, "work"),
                MediaDirectoryPath = media,
                BootWimPath = bootWim,
                MountDirectoryPath = Path.Combine(root, "mount"),
                DriverWorkspacePath = Path.Combine(root, "drivers"),
                LogsDirectoryPath = Path.Combine(root, "logs"),
                MakeWinPeMediaPath = "MakeWinPEMedia.cmd",
                DismPath = "dism.exe",
                Architecture = WinPeArchitecture.X64
            };

            var tools = new WinPeToolPaths
            {
                MakeWinPeMediaPath = "MakeWinPEMedia.cmd",
                DismPath = "dism.exe"
            };

            return new TempPreparedWorkspace(
                root,
                new WinPeWorkspacePreparationResult
                {
                    Artifact = artifact,
                    Tools = tools,
                    UseBootEx = useBootEx
                });
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed class FakeIsoRunner : IWinPeProcessRunner
    {
        private readonly WinPeProcessExecution? result;
        private readonly Exception? exception;

        public FakeIsoRunner()
        {
        }

        public FakeIsoRunner(WinPeProcessExecution result)
        {
            this.result = result;
        }

        public FakeIsoRunner(Exception exception)
        {
            this.exception = exception;
        }

        public List<WinPeProcessExecution> Executions { get; } = [];

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
            throw new NotSupportedException();
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

            if (result is not null)
            {
                return Task.FromResult(result with
                {
                    FileName = scriptPath,
                    Arguments = scriptArguments,
                    WorkingDirectory = workingDirectory
                });
            }

            string outputIsoPath = ExtractLastIsoArgument(scriptArguments);
            Directory.CreateDirectory(Path.GetDirectoryName(outputIsoPath)!);
            File.WriteAllText(outputIsoPath, "iso");

            var execution = new WinPeProcessExecution
            {
                FileName = scriptPath,
                Arguments = scriptArguments,
                WorkingDirectory = workingDirectory
            };

            Executions.Add(execution);
            return Task.FromResult(execution);
        }

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
        }

        private static string ExtractLastIsoArgument(string arguments)
        {
            string[] quotedValues = arguments.Split('"');
            Assert.True(quotedValues.Length >= 5, "Expected quoted workspace and ISO paths.");
            return quotedValues[3];
        }
    }
}
