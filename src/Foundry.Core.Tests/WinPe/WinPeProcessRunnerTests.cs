// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Foundry.Core.Services.WinPe;
using Foundry.Core.Tests.TestUtilities;
using Foundry.Utilities.Processes;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_PreservesExecutableArgumentBoundaries()
    {
        using var workspace = new TemporaryDirectory();
        string[] values = ["", "space value", "C:\\path with spaces\\", "embedded\"quote", "é漢字", "&|<>^%!()"];

        WinPeProcessExecution result = await new WinPeProcessRunner().RunAsync(
            GetChildPath(), ["argv", .. values], workspace.Path, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.ToDiagnosticText());
        Assert.Equal(values, JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim()));
    }

    [Fact]
    public async Task RunWithOutputAsync_PreservesBoundedTailsAndCallbacks()
    {
        using var workspace = new TemporaryDirectory();
        int maximumSegment = 0;
        WinPeProcessExecution result = await new WinPeProcessRunner().RunWithOutputAsync(
            GetChildPath(), ["large-output", "single-line"], workspace.Path,
            line => maximumSegment = Math.Max(maximumSegment, line.Length), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.Equal(1_048_576, result.StandardOutput.Length);
        Assert.Equal(1_048_576, result.StandardError.Length);
        Assert.InRange(maximumSegment, 1, 16_384);
        Assert.EndsWith("stdout-tail", result.StandardOutput.TrimEnd());
        Assert.EndsWith("stderr-tail", result.StandardError.TrimEnd());
        Assert.Throws<InvalidDataException>(result.EnsureCompleteOutput);
    }

    [Fact]
    public async Task RunAsync_EnforcesTheOwningOperationDeadline()
    {
        using var workspace = new TemporaryDirectory();
        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() => new WinPeProcessRunner().RunAsync(
            "powershell.exe", ["-NoProfile", "-NonInteractive", .. PowerShellCommand.CreateEncodedArguments("Start-Sleep -Seconds 60")],
            workspace.Path, TestContext.Current.CancellationToken, executionTimeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(true, exception.Data["ProcessRootExitConfirmed"]);
        Assert.Equal(false, exception.Data["ProcessTreeTerminationConfirmed"]);
    }

    [Fact]
    public async Task GetUsbCandidatesAsync_RejectsAnActuallyTruncatedValidJsonSuffix()
    {
        using var workspace = new TemporaryDirectory();
        WinPeProcessExecution captured = await new WinPeProcessRunner().RunAsync(
            "powershell.exe", ["-NoProfile", "-NonInteractive", .. PowerShellCommand.CreateEncodedArguments("[Console]::Write((' ' * 1048577)); [Console]::Write('[]')")],
            workspace.Path, TestContext.Current.CancellationToken);
        Assert.True(captured.StandardOutputTruncated);
        Assert.Empty(JsonSerializer.Deserialize<string[]>(captured.StandardOutput)!);
        var runner = new CapturedOutputRunner(captured);

        WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>> result = await new WinPeUsbMediaService(runner).GetUsbCandidatesAsync(
            new WinPeToolPaths { PowerShellPath = "must-not-run.exe" }, workspace.Path, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbQueryFailed, result.Error?.Code);
        Assert.Equal(TimeSpan.FromMinutes(2), runner.LastTimeout);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IsBootExSupportedAsync_RejectsTruncatedHelp(bool outputTruncated, bool errorTruncated)
    {
        using var workspace = new TemporaryDirectory();
        var runner = new CapturedOutputRunner(new WinPeProcessExecution
        {
            StandardOutput = "/bootex",
            StandardOutputTruncated = outputTruncated,
            StandardErrorTruncated = errorTruncated
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => new WinPeToolResolver().IsBootExSupportedAsync(
            new WinPeToolPaths { MakeWinPeMediaPath = "must-not-run.cmd" }, runner, workspace.Path, TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromMinutes(2), runner.LastTimeout);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunCmdScriptAsync_PreservesSupportedBatchValues(bool direct, bool initializeAdkEnvironment)
    {
        using var workspace = new TemporaryDirectory();
        string adkDirectory = Path.Combine(workspace.Path, "Program Files (x86)", "ADK tools é漢字");
        string scriptDirectory = Path.Combine(adkDirectory, "Windows Preinstallation Environment");
        Directory.CreateDirectory(scriptDirectory);
        if (initializeAdkEnvironment)
        {
            string deploymentTools = Path.Combine(adkDirectory, "Deployment Tools");
            Directory.CreateDirectory(deploymentTools);
            await File.WriteAllTextAsync(Path.Combine(deploymentTools, "DandISetEnv.bat"), "@echo off\r\nset CORE_BATCH_ADK_ENV=ready\r\n", TestContext.Current.CancellationToken);
        }

        string scriptPath = Path.Combine(scriptDirectory, "capture arguments.cmd");
        await File.WriteAllTextAsync(scriptPath, """
            @echo off
            set "ARG0=%~1"
            set "ARG1=%~2"
            set "ARG2=%~3"
            set "ARG3=%~4"
            powershell.exe -NoProfile -NonInteractive -Command "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); ConvertTo-Json -Compress -InputObject @([string]$env:ARG0, [string]$env:ARG1, [string]$env:ARG2, [string]$env:ARG3, [string]$env:CORE_BATCH_ADK_ENV)"
            """, Encoding.ASCII, TestContext.Current.CancellationToken);
        const string arguments = "\"\" \"space value\" \"C:\\Program Files (x86)\\é漢字\\\" plain";
        var runner = new WinPeProcessRunner();

        WinPeProcessExecution result = direct
            ? await runner.RunCmdScriptDirectAsync(scriptPath, arguments, workspace.Path, TestContext.Current.CancellationToken)
            : await runner.RunCmdScriptAsync(scriptPath, arguments, workspace.Path, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.ToDiagnosticText());
        Assert.Equal(["", "space value", "C:\\Program Files (x86)\\é漢字\\", "plain", initializeAdkEnvironment ? "ready" : ""], JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim())!);
    }

    [Theory]
    [InlineData("%COMSPEC%")]
    [InlineData("!VALUE!")]
    [InlineData("value^value")]
    [InlineData("value & echo injected>marker")]
    [InlineData("value|echo injected")]
    [InlineData("value<source")]
    [InlineData("value>marker")]
    [InlineData("value\r\necho injected>marker")]
    [InlineData("(value)")]
    [InlineData("\"unterminated")]
    public async Task RunCmdScriptAsync_RejectsUnsupportedGrammarBeforeStarting(string arguments)
    {
        using var workspace = new TemporaryDirectory();
        string scriptPath = Path.Combine(workspace.Path, "mark.cmd");
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\necho started>started\r\n", TestContext.Current.CancellationToken);

        foreach (bool direct in new[] { false, true })
        {
            var runner = new WinPeProcessRunner();
            await Assert.ThrowsAsync<ArgumentException>(() => direct
                ? runner.RunCmdScriptDirectAsync(scriptPath, arguments, workspace.Path, TestContext.Current.CancellationToken)
                : runner.RunCmdScriptAsync(scriptPath, arguments, workspace.Path, TestContext.Current.CancellationToken));
        }

        Assert.False(File.Exists(Path.Combine(workspace.Path, "started")));
        Assert.False(File.Exists(Path.Combine(workspace.Path, "marker")));
    }

    [Fact]
    public void ProcessExecution_PreservesTruncatedDiagnosticMarkers()
    {
        WinPeProcessExecution execution = WinPeProcessExecution.FromProcessExecutionResult(new ProcessExecutionResult
        {
            StandardOutput = "plausible suffix",
            StandardError = "error suffix",
            StandardOutputTruncated = true,
            StandardErrorTruncated = true
        });

        Assert.Contains("StdOut (truncated tail):", execution.ToDiagnosticText());
        Assert.Contains("StdErr (truncated tail):", execution.ToDiagnosticText());
    }

    [Fact]
    public async Task RunWithOutputAsync_PreservesRawExecutionAndCallbacks()
    {
        using var workspace = new TemporaryDirectory();
        var outputLines = new List<string>();
        var errorLines = new List<string>();
        const string arguments = "/d /s /c \"echo stdout & echo stderr 1>&2 & exit /b 7\"";

        WinPeProcessExecution result = await new WinPeProcessRunner().RunWithOutputAsync(
            GetCommandProcessor(),
            arguments,
            workspace.Path,
            outputLines.Add,
            errorLines.Add,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(arguments, result.Arguments);
        Assert.Equal("stdout", result.StandardOutput.Trim());
        Assert.Equal("stderr", result.StandardError.Trim());
        Assert.Equal(["stdout "], outputLines);
        Assert.Equal(["stderr  "], errorLines);
    }

    [Fact]
    public async Task RunAsync_FiltersReservedEnvironmentOverrides()
    {
        using var workspace = new TemporaryDirectory();
        string normalName = $"CORE_PROCESS_{Guid.NewGuid():N}";
        string reservedName = $"FOUNDRY_CORE_PROCESS_{Guid.NewGuid():N}";
        string arguments = $"/d /s /c \"echo %{normalName}% & if defined {reservedName} (echo leaked) else echo filtered\"";
        var environment = new Dictionary<string, string>
        {
            [normalName] = "normal-value",
            [reservedName] = "reserved-value"
        };

        WinPeProcessExecution result = await new WinPeProcessRunner().RunAsync(
            GetCommandProcessor(),
            arguments,
            workspace.Path,
            TestContext.Current.CancellationToken,
            environment);

        Assert.True(result.IsSuccess);
        Assert.Contains("normal-value", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("filtered", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithPreCanceledToken_DoesNotCreateWorkingDirectory()
    {
        using var workspace = new TemporaryDirectory();
        string workingDirectory = Path.Combine(workspace.Path, "must-not-exist");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WinPeProcessRunner().RunAsync(
                GetCommandProcessor(),
                "/d /c exit",
                workingDirectory,
                cancellation.Token));

        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task RunAsync_WhenExecutableCannotStart_PreservesWin32Exception()
    {
        using var workspace = new TemporaryDirectory();
        string executablePath = Path.Combine(workspace.Path, "missing.exe");

        await Assert.ThrowsAsync<Win32Exception>(() =>
            new WinPeProcessRunner().RunAsync(
                executablePath,
                string.Empty,
                workspace.Path,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunCmdScriptAsync_ExecutesTheTargetScript(bool direct)
    {
        using var workspace = new TemporaryDirectory();
        string scriptPath = Path.Combine(workspace.Path, "script with spaces.cmd");
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\necho script:%~1\r\n",
            TestContext.Current.CancellationToken);
        var runner = new WinPeProcessRunner();

        WinPeProcessExecution result = direct
            ? await runner.RunCmdScriptDirectAsync(
                scriptPath,
                "value",
                workspace.Path,
                TestContext.Current.CancellationToken)
            : await runner.RunCmdScriptAsync(
                scriptPath,
                "value",
                workspace.Path,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("script:value", result.StandardOutput.Trim());
    }

    private static string GetChildPath() => Path.Combine(AppContext.BaseDirectory, "ProcessTestChild", "ProcessTestChild.exe");

    private sealed class CapturedOutputRunner(WinPeProcessExecution result) : IWinPeProcessRunner
    {
        public TimeSpan? LastTimeout { get; private set; }

        public Task<WinPeProcessExecution> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environmentOverrides = null, TimeSpan? executionTimeout = null)
        {
            LastTimeout = executionTimeout;
            return Task.FromResult(result);
        }

        public Task<WinPeProcessExecution> RunAsync(string fileName, string arguments, string workingDirectory,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environmentOverrides = null, TimeSpan? executionTimeout = null) => throw new NotSupportedException();

        public Task<WinPeProcessExecution> RunCmdScriptAsync(string scriptPath, string scriptArguments, string workingDirectory,
            CancellationToken cancellationToken, TimeSpan? executionTimeout = null) => throw new NotSupportedException();

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(string scriptPath, string scriptArguments, string workingDirectory,
            CancellationToken cancellationToken, TimeSpan? executionTimeout = null)
        {
            LastTimeout = executionTimeout;
            return Task.FromResult(result);
        }
    }

    private static string GetCommandProcessor()
    {
        string? commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrWhiteSpace(commandProcessor) ? @"C:\Windows\System32\cmd.exe" : commandProcessor;
    }
}
