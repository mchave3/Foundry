// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.Hardware;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class PreparedDeploymentLifecycleTests
{
    [Fact]
    public async Task PrepareTargetDiskAsync_RejectsTruncatedPartitionJsonBeforeRetainingLayout()
    {
        string directory = Directory.CreateTempSubdirectory("foundry-partition-output-").FullName;
        try
        {
            var runner = new LifecycleRunner(false, truncatePreparationOutput: true);
            var service = new WindowsDeploymentService(runner, NullLogger<WindowsDeploymentService>.Instance,
                () => WindowsFirmwareType.Uefi, _ => Assert.Fail("Partition attributes must not be changed after incomplete output."));
            var expected = new TargetDiskIdentity(9, "confirmed-disk", "confirmed-serial", 137438953472, "NVMe");

            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareTargetDiskAsync(expected, directory, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureBootAsync(VolumePathDiagnosticTests.WindowsRoot,
                VolumePathDiagnosticTests.SystemRoot, 26200, directory, TestContext.Current.CancellationToken));
            Assert.Equal(["prepare"], runner.Events);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PublicBootFailure_RedactsDiagnosticCopyAndPreservesExecutionPaths()
    {
        string directory = Directory.CreateTempSubdirectory("foundry-boot-failure-").FullName;
        try
        {
            var runner = new LifecycleRunner(false, failBoot: true);
            var service = new WindowsDeploymentService(runner, NullLogger<WindowsDeploymentService>.Instance,
                () => WindowsFirmwareType.Uefi, _ => { }, fileExists: _ => true);
            DeploymentTargetLayout layout = await service.PrepareTargetDiskAsync(
                new TargetDiskIdentity(9, "confirmed-disk", "confirmed-serial", 137438953472, "NVMe"), directory, TestContext.Current.CancellationToken);
            DeploymentProcessException failure = await Assert.ThrowsAsync<DeploymentProcessException>(() => service.ConfigureBootAsync(
                layout.WindowsPartitionRoot, layout.SystemPartitionRoot, 26200, directory, TestContext.Current.CancellationToken));
            VolumePathDiagnosticTests.AssertNoIdentifiers(failure.Message);
            Assert.Contains("ExitCode: 13", failure.Message, StringComparison.Ordinal);
            Assert.Contains("bcdboot.exe", failure.Message, StringComparison.Ordinal);
            Assert.Contains(VolumePathDiagnosticTests.WindowsRoot, runner.BootExecutable!, StringComparison.Ordinal);
            Assert.Equal(Path.Combine(VolumePathDiagnosticTests.WindowsRoot, "Windows"), runner.BootArguments![0]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicLifecycle_PublishesOnlyRecoveryValidatedLayout(bool rejectRecovery)
    {
        string directory = Directory.CreateTempSubdirectory("foundry-layout-lifecycle-").FullName;
        try
        {
            var runner = new LifecycleRunner(rejectRecovery);
            var service = new WindowsDeploymentService(runner, NullLogger<WindowsDeploymentService>.Instance,
                () => WindowsFirmwareType.Uefi, partition =>
                {
                    Assert.Equal(VolumePathDiagnosticTests.RecoveryRoot, partition.VolumeRoot);
                    Assert.Equal(5368709120UL, partition.Size);
                    runner.Events.Add("native-attributes");
                }, fileExists: _ => true);
            var expected = new TargetDiskIdentity(9, "confirmed-disk", "confirmed-serial", 137438953472, "NVMe");
            if (rejectRecovery)
            {
                await Assert.ThrowsAsync<DeploymentProcessException>(() => service.PrepareTargetDiskAsync(expected, directory, TestContext.Current.CancellationToken));
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureBootAsync(VolumePathDiagnosticTests.WindowsRoot,
                    VolumePathDiagnosticTests.SystemRoot, 26200, directory, TestContext.Current.CancellationToken));
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.SealRecoveryPartitionAsync(VolumePathDiagnosticTests.RecoveryRoot,
                    'R', directory, TestContext.Current.CancellationToken));
                Assert.Equal(["prepare", "validate-recovery"], runner.Events);
                return;
            }
            DeploymentTargetLayout layout = await service.PrepareTargetDiskAsync(expected, directory, TestContext.Current.CancellationToken);
            Assert.Same(expected, layout.DiskIdentity);
            Assert.Equal(VolumePathDiagnosticTests.WindowsRoot, layout.WindowsPartitionRoot);
            await service.ConfigureBootAsync(layout.WindowsPartitionRoot, layout.SystemPartitionRoot, 26200, directory, TestContext.Current.CancellationToken);
            await service.SealRecoveryPartitionAsync(layout.RecoveryPartitionRoot, layout.RecoveryPartitionLetter, directory, TestContext.Current.CancellationToken);
            Assert.Equal(["prepare", "validate-recovery", "native-attributes", "validate-windows", "validate-system", "bcdboot", "seal-recovery"], runner.Events);
            Assert.Equal(new[] { Path.Combine(VolumePathDiagnosticTests.WindowsRoot, "Windows"), "/s", "S:", "/f", "UEFI", "/c", "/bootex", "/v" }, runner.BootArguments);
            Assert.Equal(Path.Combine(VolumePathDiagnosticTests.WindowsRoot, "Windows", "System32", "bcdboot.exe"), runner.BootExecutable);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class LifecycleRunner(bool rejectRecovery, bool failBoot = false, bool truncatePreparationOutput = false) : IProcessRunner
    {
        private int _storageCalls;
        public List<string> Events { get; } = [];
        public string[]? BootArguments { get; private set; }
        public string? BootExecutable { get; private set; }
        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null) => throw new InvalidOperationException("Expected token arguments.");
        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null) =>
            RunAsync(fileName, arguments, workingDirectory, null, null, cancellationToken);
        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory,
            Action<string>? onOutputData, Action<string>? onErrorData, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
        {
            string[] tokens = arguments.ToArray();
            if (!fileName.Equals("powershell.exe", StringComparison.Ordinal))
            {
                Events.Add("bcdboot"); BootArguments = tokens; BootExecutable = fileName;
                return Task.FromResult(new ProcessExecutionResult
                {
                    ExitCode = failBoot ? 13 : 0,
                    FileName = fileName,
                    Arguments = string.Join(' ', tokens),
                    WorkingDirectory = VolumePathDiagnosticTests.WindowsRoot + "Foundry",
                    StandardOutput = VolumePathDiagnosticTests.SystemRoot,
                    StandardError = VolumePathDiagnosticTests.RecoveryRoot
                });
            }
            int call = ++_storageCalls;
            Events.Add(call switch { 1 => "prepare", 2 => "validate-recovery", 3 => "validate-windows", 4 => "validate-system", _ => "seal-recovery" });
            string script = Encoding.Unicode.GetString(Convert.FromBase64String(tokens[Array.IndexOf(tokens, "-EncodedCommand") + 1]));
            string[] dataLines = script.Split('\n');
            using JsonDocument expectedData = ReadEncodedData(dataLines[0]);
            Assert.Equal("confirmed-disk", expectedData.RootElement.GetProperty("UniqueId").GetString());
            if (call > 1)
            {
                using JsonDocument partitionData = ReadEncodedData(dataLines[1]);
                Assert.Equal(call switch
                {
                    3 => VolumePathDiagnosticTests.WindowsRoot,
                    4 => VolumePathDiagnosticTests.SystemRoot,
                    _ => VolumePathDiagnosticTests.RecoveryRoot
                }, partitionData.RootElement.GetProperty("VolumeRoot").GetString());
            }
            string json = JsonSerializer.Serialize(new
            {
                System = Partition(VolumePathDiagnosticTests.SystemRoot, 272629760, 'S'),
                Windows = Partition(VolumePathDiagnosticTests.WindowsRoot, 100000000000, 'W'),
                Recovery = Partition(VolumePathDiagnosticTests.RecoveryRoot, 5368709120, 'R')
            });
            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = rejectRecovery && call == 2 ? 5 : 0,
                StandardOutput = call == 1 ? json : "",
                StandardOutputTruncated = truncatePreparationOutput && call == 1
            });
        }
        private static JsonDocument ReadEncodedData(string line)
        {
            string encoded = line.Split('\'')[1];
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
        private static DeploymentPartitionIdentity Partition(string root, ulong size, char letter) => new(Guid.NewGuid(), 1048576, size, root, letter);
    }
}
