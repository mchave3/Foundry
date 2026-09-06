// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeDriverInjectionServiceTests
{
    [Fact]
    public async Task InjectAsync_AddsEachDriverPathWithRecurse()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-injection-{Guid.NewGuid():N}");
        string mountedImagePath = Path.Combine(root, "mount é (x86)");
        string workingDirectory = Path.Combine(root, "work");
        string driverDirectory = Path.Combine(root, "drivers & packages");
        Directory.CreateDirectory(mountedImagePath);
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(driverDirectory);

        var runner = new FakeInjectionRunner();
        var service = new WinPeDriverInjectionService(runner);

        try
        {
            WinPeResult result = await service.InjectAsync(
                new WinPeDriverInjectionOptions
                {
                    MountedImagePath = mountedImagePath,
                    WorkingDirectoryPath = workingDirectory,
                    DriverPackagePaths = [driverDirectory],
                    DismExecutablePath = "dism.exe",
                    RecurseSubdirectories = true
                },
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Details);
            WinPeProcessExecution execution = Assert.Single(runner.Executions);
            Assert.Equal("dism.exe", execution.FileName);
            Assert.Equal([$"/Image:{mountedImagePath}", "/Add-Driver", $"/Driver:{driverDirectory}", "/Recurse"], Assert.Single(runner.ArgumentLists));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InjectAsync_WhenMountedImageIsMissing_ReturnsValidationFailure()
    {
        var service = new WinPeDriverInjectionService(new FakeInjectionRunner());

        WinPeResult result = await service.InjectAsync(
            new WinPeDriverInjectionOptions
            {
                MountedImagePath = "missing",
                WorkingDirectoryPath = Path.GetTempPath(),
                DriverPackagePaths = [Path.GetTempPath()]
            },
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.ValidationFailed, result.Error?.Code);
    }

    [Fact]
    public async Task InjectAsync_ReportsDismProgressFromProcessOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-injection-{Guid.NewGuid():N}");
        string mountedImagePath = Path.Combine(root, "mount");
        string workingDirectory = Path.Combine(root, "work");
        string driverDirectory = Path.Combine(root, "drivers");
        Directory.CreateDirectory(mountedImagePath);
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(driverDirectory);

        var runner = new FakeInjectionRunner(["25%", "100%"]);
        var progress = new CollectingProgress<WinPeDismProgress>();
        var service = new WinPeDriverInjectionService(runner);

        try
        {
            WinPeResult result = await service.InjectAsync(
                new WinPeDriverInjectionOptions
                {
                    MountedImagePath = mountedImagePath,
                    WorkingDirectoryPath = workingDirectory,
                    DriverPackagePaths = [driverDirectory],
                    DismExecutablePath = "dism.exe",
                    RecurseSubdirectories = true,
                    DismProgress = progress
                },
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Details);
            Assert.Collection(
                progress.Reports,
                report => Assert.Equal(25, report.Percent),
                report => Assert.Equal(100, report.Percent));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeInjectionRunner(IReadOnlyList<string>? outputLines = null) : IWinPeProcessOutputRunner
    {
        public List<IReadOnlyList<string>> ArgumentLists { get; } = [];
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
            string arguments = string.Join(' ', argumentList);
            ArgumentLists.Add(argumentList.ToArray());
            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory
            };

            Executions.Add(execution);
            return Task.FromResult(execution);
        }

        public Task<WinPeProcessExecution> RunWithOutputAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            Action<string>? onOutputData,
            Action<string>? onErrorData,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException("Executable calls must pass argument tokens.");
        }

        public Task<WinPeProcessExecution> RunWithOutputAsync(
            string fileName,
            IReadOnlyList<string> argumentList,
            string workingDirectory,
            Action<string>? onOutputData,
            Action<string>? onErrorData,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            foreach (string line in outputLines ?? [])
            {
                onOutputData?.Invoke(line);
            }

            return RunAsync(fileName, argumentList, workingDirectory, cancellationToken, environmentOverrides, executionTimeout);
        }

        public Task<WinPeProcessExecution> RunCmdScriptAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
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
    }

    private sealed class CollectingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value)
        {
            Reports.Add(value);
        }
    }
}
