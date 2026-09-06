// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class DriverPackExtractionTrustTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExtractAsync_UnconfirmedNativeInterruption_RetainsOnlyOwnedProtection(bool rootExited, bool cancellation)
    {
        string root = Path.Combine(Path.GetTempPath(), $"FoundryExtractionRetained-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string package = Path.Combine(root, "driver.exe");
        await File.WriteAllTextAsync(package, "protected fixture", TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        Exception interruption = cancellation ? new OperationCanceledException(cancelled.Token) : new TimeoutException("native fixture deadline");
        interruption.Data["ProcessRootExitConfirmed"] = rootExited;
        interruption.Data["ProcessTreeTerminationConfirmed"] = false;
        interruption.Data["ProcessOutputDrainConfirmed"] = rootExited;
        var nativeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DriverPackExtractionService(null!, null!, new InspectingProcessRunner(() =>
        {
            if (cancellation) { cancelled.Cancel(); }
            throw interruption;
        }), NullLogger<DriverPackExtractionService>.Instance, (_, _, _) => Task.CompletedTask);
        try
        {
            Exception? actual = await Record.ExceptionAsync(() => service.ExtractAsync(new DriverPackExecutionPlan
            {
                InstallMode = DriverPackInstallMode.OfflineInf,
                ExtractionMethod = DriverPackExtractionMethod.DellSelfExtractor,
                DeferredCommandKind = DeferredDriverPackageCommandKind.None,
                DownloadedPath = package,
                EffectiveFileExtension = ".exe",
                Manufacturer = "Dell",
                RequiresInfPayload = false
            }, Path.Combine(root, "extract"), cancelled.Token));
            Assert.Same(interruption, actual);
            Assert.NotNull(actual);
            Assert.Equal(rootExited, actual.Data["ProcessRootExitConfirmed"]);
            Assert.Equal(false, actual.Data["ProcessTreeTerminationConfirmed"]);
            if (cancellation) { Assert.Equal(cancelled.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken); }
            Assert.Throws<IOException>(() => File.WriteAllText(package, "replacement"));
            Assert.Throws<IOException>(() => File.Delete(package));
        }
        finally
        {
            nativeCompleted.SetResult();
            if (interruption.Data[NativeFileLease.RetainedLeaseIdsDataKey] is Guid[] ownershipIds)
            {
                foreach (Guid ownershipId in ownershipIds)
                {
                    await NativeFileLease.ReconcileRetainedAsync(ownershipId, _ => Task.FromResult(nativeCompleted.Task.IsCompleted), CancellationToken.None);
                }
            }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExtractAsync_VerificationTimeout_DoesNotRetainUnusedNativeProtection()
    {
        string root = Path.Combine(Path.GetTempPath(), $"FoundryExtractionVerification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string package = Path.Combine(root, "driver.exe");
        await File.WriteAllTextAsync(package, "protected fixture", TestContext.Current.CancellationToken);
        var interruption = new TimeoutException("Verification fixture deadline.");
        interruption.Data["ProcessRootExitConfirmed"] = false;
        interruption.Data["ProcessTreeTerminationConfirmed"] = false;
        var verificationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DriverPackExtractionService(null!, null!, new ForbiddenProcessRunner(),
            NullLogger<DriverPackExtractionService>.Instance, (_, _, _) =>
            {
                verificationCompleted.SetResult();
                return Task.FromException(interruption);
            });
        try
        {
            Exception? actual = await Record.ExceptionAsync(() => service.ExtractAsync(new DriverPackExecutionPlan
            {
                InstallMode = DriverPackInstallMode.OfflineInf,
                ExtractionMethod = DriverPackExtractionMethod.DellSelfExtractor,
                DeferredCommandKind = DeferredDriverPackageCommandKind.None,
                DownloadedPath = package,
                EffectiveFileExtension = ".exe",
                Manufacturer = "Dell",
                RequiresInfPayload = false
            }, Path.Combine(root, "extract"), TestContext.Current.CancellationToken));

            Assert.Same(interruption, actual);
            Assert.False(NativeFileLease.HasRetainedProtection(interruption));
            Assert.False(interruption.Data.Contains(NativeFileLease.RetainedLeaseIdsDataKey));
            File.WriteAllText(package, "replacement after verification failure");
            File.Delete(package);
        }
        finally
        {
            if (interruption.Data[NativeFileLease.RetainedLeaseIdsDataKey] is Guid[] ownershipIds)
            {
                foreach (Guid ownershipId in ownershipIds)
                {
                    await NativeFileLease.ReconcileRetainedAsync(ownershipId, _ => Task.FromResult(verificationCompleted.Task.IsCompleted), CancellationToken.None);
                }
            }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExtractAsync_VerifiedPackage_RemainsLockedThroughNativeCompletion()
    {
        string root = Path.Combine(Path.GetTempPath(), $"FoundryExtractionLock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string package = Path.Combine(root, "driver.exe");
        await File.WriteAllTextAsync(package, "protected fixture", TestContext.Current.CancellationToken);
        var service = new DriverPackExtractionService(null!, null!, new InspectingProcessRunner(() => Assert.Throws<IOException>(() => File.Delete(package))),
            NullLogger<DriverPackExtractionService>.Instance, (path, _, _) =>
            {
                Assert.Throws<IOException>(() => File.WriteAllText(path, "replacement"));
                return Task.CompletedTask;
            });
        try
        {
            DriverPackExtractionResult result = await service.ExtractAsync(new DriverPackExecutionPlan
            {
                InstallMode = DriverPackInstallMode.OfflineInf,
                ExtractionMethod = DriverPackExtractionMethod.DellSelfExtractor,
                DeferredCommandKind = DeferredDriverPackageCommandKind.None,
                DownloadedPath = package,
                EffectiveFileExtension = ".exe",
                Manufacturer = "Dell",
                RequiresInfPayload = false
            }, Path.Combine(root, "extract"), TestContext.Current.CancellationToken);
            Assert.NotNull(result.ExtractedDirectoryPath);
            Assert.Equal("protected fixture", await File.ReadAllTextAsync(package, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExtractAsync_UnsignedDellPackage_CannotLaunch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"FoundryExtractionTrust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string packagePath = Path.Combine(root, "driver.exe");
        await File.WriteAllTextAsync(packagePath, "unsigned fixture", TestContext.Current.CancellationToken);
        var service = new DriverPackExtractionService(null!, null!, new ForbiddenProcessRunner(), NullLogger<DriverPackExtractionService>.Instance);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractAsync(new DriverPackExecutionPlan
            {
                InstallMode = DriverPackInstallMode.OfflineInf,
                ExtractionMethod = DriverPackExtractionMethod.DellSelfExtractor,
                DeferredCommandKind = DeferredDriverPackageCommandKind.None,
                DownloadedPath = packagePath,
                EffectiveFileExtension = ".exe",
                Manufacturer = "Dell",
                RequiresInfPayload = false
            }, Path.Combine(root, "extract"), TestContext.Current.CancellationToken));
            Assert.Equal("unsigned fixture", await File.ReadAllTextAsync(packagePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class ForbiddenProcessRunner : InspectingProcessRunner
    {
        public ForbiddenProcessRunner() : base(() => throw new InvalidOperationException("Unverified executable was launched.")) { }
    }

    private class InspectingProcessRunner(Action inspect) : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
            => throw new InvalidOperationException("Unverified executable was launched.");

        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
        {
            inspect();
            return Task.FromResult(new ProcessExecutionResult());
        }

        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, Action<string>? onOutputData, Action<string>? onErrorData, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
            => throw new InvalidOperationException("Unverified executable was launched.");
    }
}
