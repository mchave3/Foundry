// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Connect.Services.System;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Connect.Tests;

public sealed class ConnectProcessExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_PreservesNetworkArgumentBoundaries()
    {
        string[] expected = ["wlan", "connect", "name=Network \"quoted\" 日本語\\", "&|<>^%!"];
        var executor = new ConnectProcessExecutor(NullLogger<ConnectProcessExecutor>.Instance);

        ProcessExecutionResult result = await executor.ExecuteAsync(
            Path.Combine(AppContext.BaseDirectory, "ProcessTestChild", "ProcessTestChild.exe"),
            ["argv", .. expected], TestContext.Current.CancellationToken);

        Assert.Equal(expected, System.Text.Json.JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PropagatesExecutionDeadline(bool rawArguments)
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"foundry-connect-deadline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(5));
        var executor = new ConnectProcessExecutor(NullLogger<ConnectProcessExecutor>.Instance);
        try
        {
            string childPath = Path.Combine(AppContext.BaseDirectory, "ProcessTestChild", "ProcessTestChild.exe");
            TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() => rawArguments
                ? executor.ExecuteAsync(childPath, $"pipe-child \"{workspace}\"", guard.Token, TimeSpan.FromMilliseconds(300))
                : executor.ExecuteAsync(childPath, ["pipe-child", workspace], guard.Token, TimeSpan.FromMilliseconds(300)));
            Assert.Equal(true, exception.Data["ProcessRootExitConfirmed"]);
            Assert.False(guard.IsCancellationRequested);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "release-child"), string.Empty, CancellationToken.None);
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PreservesCallerCancellation()
    {
        var executor = new ConnectProcessExecutor(NullLogger<ConnectProcessExecutor>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            Path.Combine(AppContext.BaseDirectory, "ProcessTestChild", "ProcessTestChild.exe"),
            ["argv"], cancellation.Token, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ExecuteAsync_CapturesOutputAndExitCode()
    {
        var executor = new ConnectProcessExecutor(NullLogger<ConnectProcessExecutor>.Instance);
        string commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");

        ProcessExecutionResult result = await executor.ExecuteAsync(
            commandProcessor,
            "/d /c \"echo connected & echo failed 1>&2 & exit /b 7\"",
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("connected", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("failed", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExecutableCannotStart_ReturnsFailureResult()
    {
        var executor = new ConnectProcessExecutor(NullLogger<ConnectProcessExecutor>.Instance);
        string missingExecutable = Path.Combine(Path.GetTempPath(), $"foundry-missing-{Guid.NewGuid():N}.exe");

        ProcessExecutionResult result = await executor.ExecuteAsync(
            missingExecutable,
            string.Empty,
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.NotEmpty(result.StandardError);
    }
}
