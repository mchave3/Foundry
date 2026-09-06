// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Deployment.Unattend;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.Processes;

namespace Foundry.Deploy.Tests;

public sealed class OfflineRegistryWriterTests
{
    [Fact]
    public async Task WithLoadedHiveAsync_WhenRegFails_IncludesCompleteLocalProcessDiagnostic()
    {
        var result = new ProcessExecutionResult
        {
            FileName = "reg.exe",
            Arguments = "LOAD HKLM\\Foundry C:\\Windows\\System32\\config\\SOFTWARE",
            WorkingDirectory = "C:\\Work",
            ExitCode = 5,
            StandardOutput = "stdout details",
            StandardError = "access denied"
        };
        var writer = new OfflineRegistryWriter(new StubProcessRunner(result));

        DeploymentProcessException exception = await Assert.ThrowsAsync<DeploymentProcessException>(() =>
            writer.WithLoadedHiveAsync(
                "HKLM\\Foundry",
                "C:\\Windows\\System32\\config\\SOFTWARE",
                "C:\\Work",
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken));

        Assert.Contains(result.ToDiagnosticText(), exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubProcessRunner(ProcessExecutionResult result) : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null) =>
            Task.FromResult(result);

        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null) =>
            Task.FromResult(result);

        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, Action<string>? onOutputData, Action<string>? onErrorData, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null) =>
            Task.FromResult(result);
    }
}
