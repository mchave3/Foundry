// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Foundry.Utilities.Processes;
using Foundry.Utilities.Tests.IO;

namespace Foundry.Utilities.Tests.Processes;

public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan FixtureReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HangGuardTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitForOutputAsync_WhenOneReaderFails_ReportsFailureBeforeTheOtherReaderCloses(bool standardOutputFails)
    {
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task failed = Task.FromException(new IOException("reader failed"));
        Task completion = standardOutputFails
            ? ProcessRunner.WaitForOutputAsync(failed, blocked.Task)
            : ProcessRunner.WaitForOutputAsync(blocked.Task, failed);
        try
        {
            IOException error = await Assert.ThrowsAsync<IOException>(() => completion.WaitAsync(
                TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
            Assert.Equal("reader failed", error.Message);
            Assert.False(blocked.Task.IsCompleted);
        }
        finally
        {
            blocked.TrySetResult(true);
            _ = await Record.ExceptionAsync(() => completion);
        }
    }

    [Fact]
    public void ToDiagnosticText_WhenTruncatedTailsContainOnlyWhitespace_StillShowsTruncation()
    {
        var result = new ProcessExecutionResult
        {
            StandardOutput = " \r\n ",
            StandardError = "\t\n",
            StandardOutputTruncated = true,
            StandardErrorTruncated = true
        };

        string diagnostic = result.ToDiagnosticText();

        Assert.Contains("StdOut (truncated tail):", diagnostic, StringComparison.Ordinal);
        Assert.Contains("StdErr (truncated tail):", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lines")]
    [InlineData("single-line")]
    public async Task RunAsync_WhenOutputExceedsLimit_RetainsBoundedTailsAndCallbacks(string outputMode)
    {
        using var workspace = new TemporaryDirectory();
        int maximumCallbackLength = 0;
        var request = new ProcessExecutionRequest(
            GetProcessTestChildPath(), ["large-output", outputMode], workspace.Path)
        {
            MaxCapturedOutputCharacters = 256,
            OnOutputData = line => maximumCallbackLength = Math.Max(maximumCallbackLength, line.Length)
        };

        ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(256, result.StandardOutput.Length);
        Assert.Equal(256, result.StandardError.Length);
        Assert.EndsWith("stdout-tail" + Environment.NewLine, result.StandardOutput);
        Assert.EndsWith("stderr-tail" + Environment.NewLine, result.StandardError);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.InRange(maximumCallbackLength, 1, 16_384);
        Assert.Throws<InvalidDataException>(result.EnsureCompleteOutput);
    }

    [Fact]
    public async Task RunAsync_WithArgumentList_PassesArgumentsVerbatim()
    {
        using var workspace = new TemporaryDirectory();
        string[] expectedArguments =
        [
            "",
            @"C:\folder with spaces\file.txt",
            "ends-with-backslash\\",
            "Zażółć gęślą jaźń 日本語",
            "value \"quoted\"",
            "&|<>^%!$()"
        ];
        var request = new ProcessExecutionRequest(
            GetProcessTestChildPath(),
            ["argv", .. expectedArguments],
            workspace.Path);

        ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.ToDiagnosticText());
        string[] receivedArguments = JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim())!;
        Assert.Equal(expectedArguments, receivedArguments);
    }

    [Fact]
    public async Task RunAsync_WithArgumentList_PreservesWhitespaceInArgument()
    {
        using var workspace = new TemporaryDirectory();
        string searchRoot = Path.Combine(workspace.Path, "folder with spaces");
        Directory.CreateDirectory(searchRoot);
        string markerPath = Path.Combine(searchRoot, "marker.txt");
        string searchArgument = searchRoot + Path.DirectorySeparatorChar;
        await File.WriteAllTextAsync(markerPath, "marker", TestContext.Current.CancellationToken);
        var request = new ProcessExecutionRequest(
            Path.Combine(Environment.SystemDirectory, "where.exe"),
            ["/R", searchArgument, "marker.txt"],
            workspace.Path);

        ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(markerPath, result.StandardOutput.Trim(), ignoreCase: true);
        Assert.Equal($"/R \"{searchArgument}\\\" marker.txt", result.Arguments);
    }

    [Fact]
    public async Task RunAsync_WithQuoteInArgument_EscapesDiagnosticDisplay()
    {
        using var workspace = new TemporaryDirectory();
        var request = new ProcessExecutionRequest(
            GetCommandProcessor(),
            ["/d", "/s", "/c", "echo", "value \"quoted\""],
            workspace.Path);

        ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("/d /s /c echo \"value \\\"quoted\\\"\"", result.Arguments);
    }

    [Fact]
    public async Task RunAsync_WithRawArguments_CapturesBothStreamsAndNonZeroExit()
    {
        using var workspace = new TemporaryDirectory();
        ProcessExecutionRequest request = ProcessExecutionRequest.FromRawArguments(
            GetCommandProcessor(),
            "/d /s /c \"echo stdout & echo stderr 1>&2 & exit /b 7\"",
            workspace.Path);

        ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("stdout", result.StandardOutput.Trim());
        Assert.Equal("stderr", result.StandardError.Trim());
        Assert.Equal(request.RawArguments, result.Arguments);
    }

    [Fact]
    public async Task RunAsync_WhenCallbacksThrow_StillCapturesOutput()
    {
        using var workspace = new TemporaryDirectory();
        var errorLines = new List<string>();
        ProcessExecutionRequest request = ProcessExecutionRequest.FromRawArguments(
            GetCommandProcessor(),
            "/d /s /c \"(echo stdout) & (echo stderr) 1>&2\"",
            workspace.Path) with
        {
            OnOutputData = _ => throw new InvalidOperationException("callback failure"),
            OnErrorData = errorLines.Add
        };

        ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("stdout", result.StandardOutput.Trim());
        Assert.Equal("stderr", result.StandardError.Trim());
        Assert.Equal(["stderr"], errorLines);
    }

    [Fact]
    public async Task RunAsync_CreatesAndUsesWorkingDirectory()
    {
        using var workspace = new TemporaryDirectory();
        string workingDirectory = Path.Combine(workspace.Path, "created", "nested");
        ProcessExecutionRequest request = ProcessExecutionRequest.FromRawArguments(
            GetCommandProcessor(),
            "/d /s /c cd",
            workingDirectory);

        ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(workingDirectory));
        Assert.Equal(workingDirectory, result.StandardOutput.Trim(), ignoreCase: true);
        Assert.Equal(workingDirectory, result.WorkingDirectory);
    }

    [Fact]
    public async Task RunAsync_AppliesEnvironmentOverrides()
    {
        using var workspace = new TemporaryDirectory();
        ProcessExecutionRequest request = ProcessExecutionRequest.FromRawArguments(
            GetCommandProcessor(),
            "/d /s /c echo %FOUNDRY_PROCESS_TEST%",
            workspace.Path) with
        {
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["FOUNDRY_PROCESS_TEST"] = "expected value"
            }
        };

        ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("expected value", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task RunAsync_WithPreCanceledToken_DoesNotCreateWorkingDirectory()
    {
        using var workspace = new TemporaryDirectory();
        string workingDirectory = Path.Combine(workspace.Path, "must-not-exist");
        ProcessExecutionRequest request = ProcessExecutionRequest.FromRawArguments(
            GetCommandProcessor(),
            "/d /s /c echo should-not-run",
            workingDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(request, cancellation.Token));

        Assert.False(Directory.Exists(workingDirectory));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RunAsync_WhenInterrupted_ReapsRootAndBoundsInheritedOutput(bool rootExits, bool cancel)
    {
        var workspace = new TemporaryDirectory();
        var rootReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ProcessExecutionRequest(
            GetProcessTestChildPath(),
            ["pipe-root", workspace.Path],
            workspace.Path) with
        {
            TerminationGracePeriod = TimeSpan.FromSeconds(1),
            ExecutionTimeout = rootExits || cancel ? null : TimeSpan.FromSeconds(5),
            OnOutputData = line =>
            {
                if (line.Equals("root-ready", StringComparison.Ordinal))
                {
                    rootReady.TrySetResult(true);
                }
            }
        };
        using var cancellation = new CancellationTokenSource();
        Task<ProcessExecutionResult>? executionTask = null;
        bool testBodyCompleted = false;

        try
        {
            executionTask = new ProcessRunner().RunAsync(request, cancellation.Token);
            await rootReady.Task.WaitAsync(FixtureReadyTimeout, TestContext.Current.CancellationToken);

            ProcessIdentity rootIdentity = ReadIdentity(Path.Combine(workspace.Path, "root.json"));
            ProcessIdentity childIdentity = ReadIdentity(Path.Combine(workspace.Path, "child.json"));
            using Process root = OpenOwnedProcess(rootIdentity);
            using Process child = OpenOwnedProcess(childIdentity);
            if (rootExits)
            {
                File.WriteAllText(Path.Combine(workspace.Path, "allow-root-exit"), string.Empty);
                await root.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(
                    HangGuardTimeout,
                    TestContext.Current.CancellationToken);
                Assert.True(root.HasExited);
            }
            else
            {
                Assert.False(root.HasExited);
            }

            Assert.False(child.HasExited);

            Exception interruption;
            if (cancel)
            {
                cancellation.Cancel();
                interruption = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionTask.WaitAsync(
                    HangGuardTimeout, TestContext.Current.CancellationToken));
            }
            else
            {
                interruption = await Assert.ThrowsAsync<TimeoutException>(() => executionTask.WaitAsync(
                    HangGuardTimeout, TestContext.Current.CancellationToken));
            }

            Assert.Equal(true, interruption.Data["ProcessRootExitConfirmed"]);
            Assert.Equal(false, interruption.Data["ProcessTreeTerminationConfirmed"]);
            Assert.Equal(!rootExits, interruption.Data["ProcessOutputDrainConfirmed"]);
            Assert.True(root.HasExited);
            if (!rootExits)
            {
                await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(
                    HangGuardTimeout, TestContext.Current.CancellationToken);
            }
            testBodyCompleted = true;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                await CancelAndObserveRunnerAsync(executionTask, cancellation);
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            try
            {
                await ReleaseOwnedChildAsync(workspace.Path);
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }

            try
            {
                await CancelAndObserveRunnerAsync(executionTask, cancellation);
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }

            if (executionTask is null || executionTask.IsCompleted)
            {
                try
                {
                    workspace.Dispose();
                }
                catch (Exception ex)
                {
                    cleanupFailure ??= ex;
                }
            }
            else
            {
                cleanupFailure ??= new TimeoutException(
                    "The process runner did not reach a terminal state after the owned child was released.");
            }

            if (testBodyCompleted && cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
    }

    [Fact]
    public async Task RunAsync_WhenExecutableCannotStart_ThrowsProcessStartException()
    {
        using var workspace = new TemporaryDirectory();
        var request = new ProcessExecutionRequest(
            Path.Combine(workspace.Path, "missing-executable.exe"),
            [],
            workspace.Path);

        ProcessStartException exception = await Assert.ThrowsAsync<ProcessStartException>(() =>
            new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(request.FileName, exception.FileName);
        Assert.NotNull(exception.InnerException);
        Assert.NotNull(exception.NativeErrorCode);
    }

    [Theory]
    [InlineData(0, 1000, 1000)]
    [InlineData(256, 0, 1000)]
    [InlineData(256, 1000, 0)]
    public async Task RunAsync_WithInvalidLimits_DoesNotCreateWorkingDirectory(int capture, int cleanup, int timeout)
    {
        using var workspace = new TemporaryDirectory();
        string workingDirectory = Path.Combine(workspace.Path, "must-not-exist");
        var request = new ProcessExecutionRequest(GetProcessTestChildPath(), ["argv"], workingDirectory)
        {
            MaxCapturedOutputCharacters = capture,
            TerminationGracePeriod = TimeSpan.FromMilliseconds(cleanup),
            ExecutionTimeout = TimeSpan.FromMilliseconds(timeout)
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(workingDirectory));
    }

    [Theory]
    [InlineData("", "working")]
    [InlineData("   ", "working")]
    [InlineData("cmd.exe", "")]
    [InlineData("cmd.exe", "   ")]
    public async Task RunAsync_WithBlankRequiredValue_ThrowsArgumentException(string fileName, string workingDirectory)
    {
        var request = new ProcessExecutionRequest(fileName, [], workingDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ToDiagnosticText_IncludesCommandLocationExitCodeAndNonEmptyStreams()
    {
        var result = new ProcessExecutionResult
        {
            ExitCode = 5,
            FileName = "tool.exe",
            Arguments = "--flag value",
            WorkingDirectory = @"C:\work",
            StandardOutput = " output ",
            StandardError = " error "
        };

        Assert.Equal(
            "Command: tool.exe --flag value\r\n" +
            "WorkingDirectory: C:\\work\r\n" +
            "ExitCode: 5\r\n" +
            "StdOut:\r\n" +
            "output\r\n" +
            "StdErr:\r\n" +
            "error",
            result.ToDiagnosticText());
    }

    private static string GetCommandProcessor()
    {
        string? commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrWhiteSpace(commandProcessor) ? @"C:\Windows\System32\cmd.exe" : commandProcessor;
    }

    private static string GetProcessTestChildPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "ProcessTestChild", "ProcessTestChild.exe");
    }

    private static ProcessIdentity ReadIdentity(string path)
    {
        return JsonSerializer.Deserialize<ProcessIdentity>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Process identity file '{path}' was empty.");
    }

    private static Process OpenOwnedProcess(ProcessIdentity identity)
    {
        Process process = Process.GetProcessById(identity.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks)
        {
            process.Dispose();
            throw new InvalidOperationException($"Process {identity.ProcessId} no longer matches the recorded fixture process.");
        }

        return process;
    }

    private static Process? TryOpenOwnedProcess(ProcessIdentity identity)
    {
        try
        {
            return OpenOwnedProcess(identity);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static async Task CancelAndObserveRunnerAsync(
        Task<ProcessExecutionResult>? executionTask,
        CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        if (executionTask is null)
        {
            return;
        }

        try
        {
            await executionTask.WaitAsync(CleanupTimeout);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (TimeoutException) when (executionTask.IsFaulted)
        {
        }
    }

    private static async Task ReleaseOwnedChildAsync(string workspace)
    {
        File.WriteAllText(Path.Combine(workspace, "release-child"), string.Empty);
        string identityPath = Path.Combine(workspace, "child.json");
        if (!File.Exists(identityPath))
        {
            return;
        }

        ProcessIdentity identity = ReadIdentity(identityPath);
        using Process? child = TryOpenOwnedProcess(identity);
        if (child is null)
        {
            return;
        }

        try
        {
            await child.WaitForExitAsync().WaitAsync(CleanupTimeout);
        }
        catch (TimeoutException)
        {
            if (!child.HasExited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            await child.WaitForExitAsync().WaitAsync(CleanupTimeout);
        }
    }

    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks);
}
