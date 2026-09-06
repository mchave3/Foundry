// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections;
using Foundry.Utilities.Processes;
using Foundry.Utilities.Tests.IO;

namespace Foundry.Utilities.Tests.Processes;

public sealed class NativeFileLeaseTests
{
    [Theory]
    [InlineData("timeout", false, false)]
    [InlineData("timeout", false, true)]
    [InlineData("timeout", true, false)]
    [InlineData("timeout", true, true)]
    [InlineData("cancellation", false, false)]
    [InlineData("cancellation", false, true)]
    [InlineData("cancellation", true, false)]
    [InlineData("cancellation", true, true)]
    [InlineData("io", false, false)]
    [InlineData("io", false, true)]
    [InlineData("io", true, false)]
    [InlineData("io", true, true)]
    public async Task RunAsync_WhenNativeConsumersMayRemain_RetainsProtectionAndOriginalFailure(
        string failureKind,
        bool rootExitConfirmed,
        bool outputDrainConfirmed)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception failure = CreateFailure(failureKind, cancellation.Token);
        SetCompletionMetadata(failure, rootExitConfirmed, treeTerminationConfirmed: false, outputDrainConfirmed);
        object diagnostic = new();
        failure.Data["NativeDiagnostic"] = diagnostic;
        IDictionary originalData = failure.Data;
        var fixture = new NativeConsumerFixture();
        try
        {
            Exception? actual = await fixture.CaptureFailureAsync(() => Task.FromException<int>(failure), TestContext.Current.CancellationToken);
            fixture.Lease.Dispose();

            Assert.Same(failure, actual);
            Assert.NotNull(actual);
            Assert.Same(originalData, actual.Data);
            Assert.Same(diagnostic, actual.Data["NativeDiagnostic"]);
            Assert.Equal(rootExitConfirmed, actual.Data["ProcessRootExitConfirmed"]);
            Assert.Equal(false, actual.Data["ProcessTreeTerminationConfirmed"]);
            Assert.Equal(outputDrainConfirmed, actual.Data["ProcessOutputDrainConfirmed"]);
            Assert.Equal(5, actual.Data.Count);
            Assert.NotEqual(Guid.Empty, GetOwnershipId(actual));
            Assert.True(NativeFileLease.HasRetainedProtection(actual));
            if (actual is OperationCanceledException canceled)
            {
                Assert.Equal(cancellation.Token, canceled.CancellationToken);
            }

            fixture.AssertProtected();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    public async Task RunAsync_WhenInterruptionHasNoCompletionMetadata_RetainsProtection(string failureKind)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception failure = CreateFailure(failureKind, cancellation.Token);
        var fixture = new NativeConsumerFixture();
        try
        {
            Exception? actual = await fixture.CaptureFailureAsync(() => Task.FromException<int>(failure), TestContext.Current.CancellationToken);
            fixture.Lease.Dispose();

            Assert.Same(failure, actual);
            Assert.NotNull(actual);
            Assert.Single(actual.Data);
            Assert.NotEqual(Guid.Empty, GetOwnershipId(actual));
            Assert.True(NativeFileLease.HasRetainedProtection(actual));
            if (actual is OperationCanceledException canceled)
            {
                Assert.Equal(cancellation.Token, canceled.CancellationToken);
            }

            fixture.AssertProtected();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Theory]
    [InlineData("timeout", false)]
    [InlineData("timeout", true)]
    [InlineData("cancellation", false)]
    [InlineData("cancellation", true)]
    [InlineData("io", false)]
    [InlineData("io", true)]
    public async Task RunAsync_WhenAllNativeConsumersAreConfirmedComplete_ReleasesProtectionOnDispose(
        string failureKind,
        bool outputDrainConfirmed)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception failure = CreateFailure(failureKind, cancellation.Token);
        SetCompletionMetadata(failure, rootExitConfirmed: true, treeTerminationConfirmed: true, outputDrainConfirmed);
        var fixture = new NativeConsumerFixture();
        fixture.ReleaseNativeConsumers();
        try
        {
            Exception? actual = await fixture.CaptureFailureAsync(() => Task.FromException<int>(failure), TestContext.Current.CancellationToken);
            fixture.Lease.Dispose();

            Assert.Same(failure, actual);
            Assert.NotNull(actual);
            Assert.Equal(3, actual.Data.Count);
            Assert.False(actual.Data.Contains(NativeFileLease.RetainedLeaseIdsDataKey));
            Assert.False(NativeFileLease.HasRetainedProtection(actual));
            fixture.AssertReleased();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Fact]
    public async Task RunAsync_WhenNativeCallCompletes_ProtectsUntilDisposedAndReturnsResult()
    {
        var fixture = new NativeConsumerFixture();
        try
        {
            fixture.AssertProtected();
            int result = await fixture.Lease.RunAsync(() =>
            {
                fixture.AssertProtected();
                fixture.ReleaseNativeConsumers();
                return Task.FromResult(42);
            }, TestContext.Current.CancellationToken);

            Assert.Equal(42, result);
            fixture.AssertProtected();
            fixture.Lease.Dispose();
            fixture.AssertReleased();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Fact]
    public async Task RunAsync_WhenCanceledBeforeInvocation_DoesNotInvokeNativeCallOrRetainProtection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool invoked = false;
        var fixture = new NativeConsumerFixture();
        try
        {
            Exception? actual = await fixture.CaptureFailureAsync(() =>
            {
                invoked = true;
                return Task.FromResult(42);
            }, cancellation.Token);
            fixture.Lease.Dispose();

            OperationCanceledException canceled = Assert.IsAssignableFrom<OperationCanceledException>(actual);
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.False(invoked);
            Assert.False(canceled.Data.Contains(NativeFileLease.RetainedLeaseIdsDataKey));
            Assert.False(NativeFileLease.HasRetainedProtection(canceled));
            fixture.AssertReleased();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("io")]
    public async Task RunAsync_WhenFailureHasNoStartedProcessEvidence_ReleasesProtection(string failureKind)
    {
        Exception failure = failureKind == "start"
            ? new ProcessStartException("test-native-tool.exe", "Native process did not start.")
            : new IOException("Native input could not be opened.");
        var fixture = new NativeConsumerFixture();
        try
        {
            Exception? actual = await fixture.CaptureFailureAsync(() => Task.FromException<int>(failure), TestContext.Current.CancellationToken);
            fixture.Lease.Dispose();

            Assert.Same(failure, actual);
            Assert.NotNull(actual);
            Assert.Empty(actual.Data);
            Assert.False(NativeFileLease.HasRetainedProtection(actual));
            fixture.AssertReleased();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task ReconcileRetainedAsync_WhenConfirmationFails_KeepsProtectionUntilOwnedConsumersComplete(
        string confirmationFailure)
    {
        var fixture = new NativeConsumerFixture();
        var unrelatedFixture = new NativeConsumerFixture();
        using var cancellation = new CancellationTokenSource();
        try
        {
            Exception? failure = await fixture.CaptureFailureAsync(() =>
                Task.FromException<int>(new TimeoutException("Native consumers are still running.")), TestContext.Current.CancellationToken);
            Exception? unrelatedFailure = await unrelatedFixture.CaptureFailureAsync(() =>
                Task.FromException<int>(new TimeoutException("Other native consumers are still running.")), TestContext.Current.CancellationToken);
            fixture.Lease.Dispose();
            unrelatedFixture.Lease.Dispose();
            Assert.NotNull(failure);
            Assert.NotNull(unrelatedFailure);
            Guid ownershipId = GetOwnershipId(failure);
            Guid unrelatedOwnershipId = GetOwnershipId(unrelatedFailure);
            Assert.NotEqual(ownershipId, unrelatedOwnershipId);
            if (confirmationFailure == "throw")
            {
                fixture.ConfirmationFailure = new IOException("Native completion could not be inspected.");
            }
            else if (confirmationFailure == "cancel")
            {
                cancellation.Cancel();
            }

            bool reconciled = false;
            _ = await Record.ExceptionAsync(async () =>
            {
                reconciled = await NativeFileLease.ReconcileRetainedAsync(
                    ownershipId, fixture.ConfirmCompletionAsync, cancellation.Token);
            });

            Assert.False(reconciled);
            Assert.True(NativeFileLease.HasRetainedProtection(failure));
            fixture.AssertProtected();
            unrelatedFixture.AssertProtected();

            fixture.ConfirmationFailure = null;
            fixture.ReleaseNativeConsumers();
            Assert.True(await NativeFileLease.ReconcileRetainedAsync(
                ownershipId, fixture.ConfirmCompletionAsync, TestContext.Current.CancellationToken));

            Assert.False(NativeFileLease.HasRetainedProtection(failure));
            Assert.Equal(ownershipId, GetOwnershipId(failure));
            fixture.AssertReleased();
            Assert.True(NativeFileLease.HasRetainedProtection(unrelatedFailure));
            Assert.Equal(unrelatedOwnershipId, GetOwnershipId(unrelatedFailure));
            unrelatedFixture.AssertProtected();
        }
        finally
        {
            try
            {
                await fixture.CleanupAsync();
            }
            finally
            {
                await unrelatedFixture.CleanupAsync();
            }
        }
    }

    private static Exception CreateFailure(string failureKind, CancellationToken cancellationToken) => failureKind switch
    {
        "timeout" => new TimeoutException("Native operation timed out."),
        "cancellation" => new OperationCanceledException("Native operation was canceled.", cancellationToken),
        _ => new IOException("Native process output failed.")
    };

    private static void SetCompletionMetadata(
        Exception failure,
        bool rootExitConfirmed,
        bool treeTerminationConfirmed,
        bool outputDrainConfirmed)
    {
        failure.Data["ProcessRootExitConfirmed"] = rootExitConfirmed;
        failure.Data["ProcessTreeTerminationConfirmed"] = treeTerminationConfirmed;
        failure.Data["ProcessOutputDrainConfirmed"] = outputDrainConfirmed;
    }

    private static Guid GetOwnershipId(Exception failure) =>
        Assert.Single(Assert.IsType<Guid[]>(failure.Data[NativeFileLease.RetainedLeaseIdsDataKey]));

    private sealed class NativeConsumerFixture
    {
        private readonly TemporaryDirectory workspace = new();
        private readonly List<Exception> failures = [];
        private readonly string filePath;
        private bool allNativeConsumersCompleted;

        public NativeConsumerFixture()
        {
            filePath = Path.Combine(workspace.Path, "native-input.bin");
            File.WriteAllText(filePath, "verified native input");
            Lease = NativeFileLease.OpenRead(filePath);
        }

        public NativeFileLease Lease { get; }

        public Exception? ConfirmationFailure { get; set; }

        public async Task<Exception?> CaptureFailureAsync(
            Func<Task<int>> nativeCall,
            CancellationToken cancellationToken = default)
        {
            Exception? failure = await Record.ExceptionAsync(() => Lease.RunAsync(nativeCall, cancellationToken));
            if (failure is not null)
            {
                failures.Add(failure);
            }

            return failure;
        }

        public void ReleaseNativeConsumers() => allNativeConsumersCompleted = true;

        public Task<bool> ConfirmCompletionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ConfirmationFailure is not null
                ? Task.FromException<bool>(ConfirmationFailure)
                : Task.FromResult(allNativeConsumersCompleted);
        }

        public void AssertProtected()
        {
            using (var reader = new StreamReader(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                Assert.Equal("verified native input", reader.ReadToEnd());
            }

            Assert.Throws<IOException>(() =>
            {
                using var writer = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            });
            Assert.Throws<IOException>(() => File.Delete(filePath));
        }

        public void AssertReleased()
        {
            File.WriteAllText(filePath, "replacement native input");
            Assert.Equal("replacement native input", File.ReadAllText(filePath));
            File.Delete(filePath);
            Assert.False(File.Exists(filePath));
        }

        public async Task CleanupAsync()
        {
            ConfirmationFailure = null;
            ReleaseNativeConsumers();
            Lease.Dispose();
            foreach (Exception failure in failures)
            {
                if (failure.Data[NativeFileLease.RetainedLeaseIdsDataKey] is Guid[] ownershipIds)
                {
                    foreach (Guid ownershipId in ownershipIds)
                    {
                        _ = await NativeFileLease.ReconcileRetainedAsync(
                            ownershipId, ConfirmCompletionAsync, CancellationToken.None);
                    }
                }
            }

            workspace.Dispose();
        }
    }
}
