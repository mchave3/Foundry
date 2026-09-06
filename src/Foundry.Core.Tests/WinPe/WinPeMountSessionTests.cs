// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;
using Foundry.Core.Tests.TestUtilities;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeMountSessionTests
{
    [Fact]
    public async Task MountAsync_WhenDismFails_ReturnsMountFailure()
    {
        using var tempDirectory = new TemporaryDirectory();
        var runner = new FakeWinPeProcessRunner(new WinPeProcessExecution { ExitCode = 5, StandardError = "mount failed" });

        WinPeResult<WinPeMountSession> result = await WinPeMountSession.MountAsync(
            runner,
            "dism.exe",
            "boot.wim",
            Path.Combine(tempDirectory.Path, "mount"),
            tempDirectory.Path,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.WimMountFailed, result.Error?.Code);
        Assert.Single(runner.Executions);
        Assert.Contains("/Mount-Image", runner.Executions[0].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitAsync_WhenCommitSucceeds_DoesNotDiscard()
    {
        using var tempDirectory = new TemporaryDirectory();
        var runner = new FakeWinPeProcessRunner(
            new WinPeProcessExecution { ExitCode = 0 },
            new WinPeProcessExecution { ExitCode = 0 });

        WinPeMountSession session = (await WinPeMountSession.MountAsync(
            runner,
            "dism.exe",
            "boot.wim",
            Path.Combine(tempDirectory.Path, "mount"),
            tempDirectory.Path,
            CancellationToken.None)).Value!;

        WinPeResult result = await session.CommitAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, runner.Executions.Count);
        Assert.Contains("/Commit", runner.Executions[1].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitAsync_WhenCommitFails_AttemptsDiscardAndReturnsCombinedDiagnostics()
    {
        using var tempDirectory = new TemporaryDirectory();
        var runner = new FakeWinPeProcessRunner(
            new WinPeProcessExecution { ExitCode = 0 },
            new WinPeProcessExecution { ExitCode = 7, StandardError = "commit failed" },
            new WinPeProcessExecution { ExitCode = 0, StandardOutput = "discarded" });

        WinPeMountSession session = (await WinPeMountSession.MountAsync(
            runner,
            "dism.exe",
            "boot.wim",
            Path.Combine(tempDirectory.Path, "mount"),
            tempDirectory.Path,
            CancellationToken.None)).Value!;

        WinPeResult result = await session.CommitAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.WimUnmountFailed, result.Error?.Code);
        Assert.Contains("Commit diagnostics", result.Error?.Details, StringComparison.Ordinal);
        Assert.Contains("Discard diagnostics", result.Error?.Details, StringComparison.Ordinal);
        Assert.Equal(3, runner.Executions.Count);
        Assert.Contains("/Discard", runner.Executions[2].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_WhenSessionIsStillMounted_Discards()
    {
        using var tempDirectory = new TemporaryDirectory();
        var runner = new FakeWinPeProcessRunner(
            new WinPeProcessExecution { ExitCode = 0 },
            new WinPeProcessExecution { ExitCode = 0 });

        WinPeMountSession session = (await WinPeMountSession.MountAsync(
            runner,
            "dism.exe",
            "boot.wim",
            Path.Combine(tempDirectory.Path, "mount"),
            tempDirectory.Path,
            CancellationToken.None)).Value!;

        await session.DisposeAsync();

        Assert.Equal(2, runner.Executions.Count);
        Assert.Contains("/Discard", runner.Executions[1].Arguments, StringComparison.Ordinal);
    }

    private sealed class FakeWinPeProcessRunner : IWinPeProcessRunner
    {
        private readonly Queue<WinPeProcessExecution> _results;

        public FakeWinPeProcessRunner(params WinPeProcessExecution[] results)
        {
            _results = new Queue<WinPeProcessExecution>(results);
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
            string arguments = string.Join(' ', argumentList);
            WinPeProcessExecution result = _results.Dequeue() with
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory
            };

            Executions.Add(result);
            return Task.FromResult(result);
        }

        public Task<WinPeProcessExecution> RunCmdScriptAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            return RunAsync(scriptPath, scriptArguments, workingDirectory, cancellationToken);
        }

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            return RunAsync(scriptPath, scriptArguments, workingDirectory, cancellationToken);
        }
    }
}
