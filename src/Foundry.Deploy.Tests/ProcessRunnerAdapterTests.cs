// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DeployProcessRunner = Foundry.Deploy.Services.System.ProcessRunner;
using UtilityProcessRunner = Foundry.Utilities.Processes.ProcessRunner;

namespace Foundry.Deploy.Tests;

public sealed class ProcessRunnerAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_EncodedPowerShellDataIsExcludedFromLogs(bool raw)
    {
        using var workspace = new TemporaryDirectory();
        var logger = new RecordingLogger<DeployProcessRunner>();
        var runner = new DeployProcessRunner(new UtilityProcessRunner(), logger);
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::Out.WriteLine('IDENTITY-SENTINEL')"));
        ProcessExecutionResult result = raw
            ? await runner.RunAsync(GetPowerShellPath(), $"-NoProfile -EncodedCommand {encoded}", workspace.Path, TestContext.Current.CancellationToken)
            : await runner.RunAsync(GetPowerShellPath(), new[] { "-NoProfile", "-EncodedCommand", encoded }, workspace.Path, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("IDENTITY-SENTINEL", result.StandardOutput.Trim());
        Assert.DoesNotContain(logger.Messages, message => message.Contains(encoded, StringComparison.Ordinal));
    }
    [Fact]
    public async Task RunAsync_WithRawPowerShellCommand_PreservesArgumentsAndOutput()
    {
        using var workspace = new TemporaryDirectory();
        string encodedCommand = Convert.ToBase64String(
            Encoding.Unicode.GetBytes("[Console]::Out.WriteLine('raw-output')"));
        string arguments = $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedCommand}";

        ProcessExecutionResult result = await CreateRunner().RunAsync(
            GetPowerShellPath(),
            arguments,
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(arguments, result.Arguments);
        Assert.Equal("raw-output", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task RunAsync_WithArgumentList_PreservesWhitespaceInArgument()
    {
        using var workspace = new TemporaryDirectory();
        string searchRoot = Path.Combine(workspace.Path, "folder with spaces");
        Directory.CreateDirectory(searchRoot);
        string markerPath = Path.Combine(searchRoot, "marker.txt");
        await File.WriteAllTextAsync(markerPath, "marker", TestContext.Current.CancellationToken);

        ProcessExecutionResult result = await CreateRunner().RunAsync(
            Path.Combine(Environment.SystemDirectory, "where.exe"),
            ["/R", searchRoot, "marker.txt"],
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(markerPath, result.StandardOutput.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task RunAsync_WithNonZeroExit_ReturnsCapturedResult()
    {
        using var workspace = new TemporaryDirectory();

        ProcessExecutionResult result = await CreateRunner().RunAsync(
            GetCommandProcessor(),
            "/d /s /c \"echo stdout & echo stderr 1>&2 & exit /b 7\"",
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("stdout", result.StandardOutput.Trim());
        Assert.Equal("stderr", result.StandardError.Trim());
    }

    [Fact]
    public async Task RunAsync_WhenExecutableCannotStart_ThrowsSharedException()
    {
        using var workspace = new TemporaryDirectory();
        string executablePath = Path.Combine(workspace.Path, "missing.exe");

        ProcessStartException exception = await Assert.ThrowsAsync<ProcessStartException>(() =>
            CreateRunner().RunAsync(
                executablePath,
                [],
                workspace.Path,
                TestContext.Current.CancellationToken));

        Assert.Equal(executablePath, exception.FileName);
        Assert.NotNull(exception.NativeErrorCode);
    }

    [Fact]
    public async Task RunAsync_WhenOutputCallbackThrows_LogsWarningAndReturnsCapturedOutput()
    {
        using var workspace = new TemporaryDirectory();
        string markerPath = Path.Combine(workspace.Path, "marker.txt");
        await File.WriteAllTextAsync(markerPath, "marker", TestContext.Current.CancellationToken);
        var logger = new RecordingLogger<DeployProcessRunner>();
        var runner = new DeployProcessRunner(new UtilityProcessRunner(), logger);

        ProcessExecutionResult result = await runner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "where.exe"),
            ["/R", workspace.Path, "marker.txt"],
            workspace.Path,
            _ => throw new InvalidOperationException("callback failure"),
            onErrorData: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(markerPath, result.StandardOutput.Trim(), ignoreCase: true);
        Assert.Equal(1, logger.WarningCount);
    }

    private static DeployProcessRunner CreateRunner()
    {
        return new DeployProcessRunner(
            new UtilityProcessRunner(),
            NullLogger<DeployProcessRunner>.Instance);
    }

    private static string GetCommandProcessor()
    {
        string? commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrWhiteSpace(commandProcessor) ? @"C:\Windows\System32\cmd.exe" : commandProcessor;
    }

    private static string GetPowerShellPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("FoundryDeployProcessRunner-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public int WarningCount { get; private set; }
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (logLevel == LogLevel.Warning)
            {
                WarningCount++;
            }
        }
    }
}
