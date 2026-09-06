// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeProcessRunner = Foundry.Deploy.Services.System.ProcessRunner;

namespace Foundry.Deploy.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_PreservesArgumentBoundaries()
    {
        string[] expected = ["", @"C:\Windows image\", @"\\?\Volume{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}\", "a\"b", "日本語", "&|<>^%!"];
        ProcessExecutionResult result = await CreateRunner().RunAsync(
            ChildPath, ["argv", .. expected], Path.GetTempPath(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim()));
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("tokens")]
    [InlineData("callbacks")]
    public async Task RunAsync_PropagatesExecutionDeadline(string overload)
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"foundry-runtime-deadline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(5));
        RuntimeProcessRunner runner = CreateRunner();
        try
        {
            Task<ProcessExecutionResult> run = overload switch
            {
                "raw" => runner.RunAsync(ChildPath, $"pipe-child \"{workspace}\"", workspace, guard.Token, TimeSpan.FromMilliseconds(300)),
                "tokens" => runner.RunAsync(ChildPath, ["pipe-child", workspace], workspace, guard.Token, TimeSpan.FromMilliseconds(300)),
                _ => runner.RunAsync(ChildPath, ["pipe-child", workspace], workspace, null, null, guard.Token, TimeSpan.FromMilliseconds(300))
            };

            TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() => run);
            Assert.Equal(true, exception.Data["ProcessRootExitConfirmed"]);
            Assert.False(guard.IsCancellationRequested);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "release-child"), string.Empty, CancellationToken.None);
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string ChildPath => Path.Combine(AppContext.BaseDirectory, "ProcessTestChild", "ProcessTestChild.exe");

    private static RuntimeProcessRunner CreateRunner() => new(new Foundry.Utilities.Processes.ProcessRunner(), NullLogger<RuntimeProcessRunner>.Instance);
}
