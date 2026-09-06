// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Foundry.Utilities.Networking;
using Foundry.Utilities.Tests.IO;

namespace Foundry.Utilities.Tests.Networking;

public sealed class ValidatedFileTransferTests
{
    private static readonly Uri Source = new("https://example.test/payload?token=private");
    private static readonly TransferLimits Limits = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
    private static readonly FileIntegrity UnknownIntegrity = new(null, null);
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DownloadAsync_WrongDigest_PreservesPublishedArtifact()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var client = CreateClient("replacement"u8.ToArray());
        var integrity = new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, new string('A', 64)), 11);

        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadAsync(client, destination, integrity));

        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Theory]
    [InlineData(2L)]
    [InlineData(4L)]
    public async Task DownloadAsync_WrongTrustedSize_PreservesPublishedArtifact(long size)
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var client = CreateClient("abc"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadAsync(client, destination, new FileIntegrity(null, size)));

        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Theory]
    [InlineData("SHA256", "xyz")]
    [InlineData("SHA256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz")]
    [InlineData("SHA1", "a9993e364706816aba3e25717850c26c9cd0d89")]
    [InlineData("MD5", "900150983cd24fb0d6963f7d28e17f72")]
    public async Task DownloadAsync_MalformedDigest_HasNoHttpOrDirectoryEffects(string algorithm, string hex)
    {
        using var workspace = new TemporaryDirectory();
        string directory = Path.Combine(workspace.Path, "not-created");
        var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => DownloadAsync(
            client, Path.Combine(directory, "payload.bin"), new FileIntegrity(new FileDigest(new HashAlgorithmName(algorithm), hex), null)));

        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("SHA256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("SHA1", "A9993E364706816ABA3E25717850C26C9CD0D89D")]
    public async Task DownloadAsync_ValidDigest_PublishesCompleteBytes(string algorithm, string hex)
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        using var client = CreateClient("abc"u8.ToArray());

        long published = await DownloadAsync(client, destination, new FileIntegrity(new FileDigest(new HashAlgorithmName(algorithm), hex), 3));

        Assert.Equal(3, published);
        Assert.Equal("abc", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Theory]
    [InlineData(0L, null, 5_000L, 1_000L)]
    [InlineData(-1L, null, 5_000L, 1_000L)]
    [InlineData(null, 0L, 5_000L, 1_000L)]
    [InlineData(null, -1L, 5_000L, 1_000L)]
    [InlineData(null, null, 0L, 1_000L)]
    [InlineData(null, null, -1L, 1_000L)]
    [InlineData(null, null, 5_000L, 0L)]
    [InlineData(null, null, 5_000L, -1L)]
    [InlineData(null, null, 4_294_967_295L, 1_000L)]
    [InlineData(null, null, 5_000L, 4_294_967_295L)]
    public async Task DownloadAsync_InvalidLimits_HasNoHttpOrDirectoryEffects(long? size, long? maximumBytes, long overallMs, long noProgressMs)
    {
        using var workspace = new TemporaryDirectory();
        string directory = Path.Combine(workspace.Path, "not-created");
        var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ValidatedFileTransfer.DownloadAsync(
            client, Source, Path.Combine(directory, "payload.bin"), new FileIntegrity(null, size),
            new TransferLimits(TimeSpan.FromMilliseconds(overallMs), TimeSpan.FromMilliseconds(noProgressMs), maximumBytes),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(null, null, 2L)]
    [InlineData(4L, null, null)]
    [InlineData(2L, null, null)]
    [InlineData(null, 4L, null)]
    [InlineData(null, 2L, null)]
    public async Task DownloadAsync_ChunkedLengthViolation_PreservesPublishedArtifact(long? responseLength, long? expectedSize, long? maximumBytes)
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var body = new ChunkedStream("abc"u8.ToArray(), 2);
        using var client = CreateClient(body, responseLength);

        await Assert.ThrowsAsync<InvalidDataException>(() => ValidatedFileTransfer.DownloadAsync(
            client, Source, destination, new FileIntegrity(null, expectedSize), Limits with { MaximumBytes = maximumBytes },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_DeclaredLengthExceedsLimit_DoesNotReadBody()
    {
        using var workspace = new TemporaryDirectory();
        using var body = new ChunkedStream("abc"u8.ToArray(), 2);
        using var client = CreateClient(body, 3);

        await Assert.ThrowsAsync<InvalidDataException>(() => ValidatedFileTransfer.DownloadAsync(
            client, Source, Path.Combine(workspace.Path, "payload.bin"), UnknownIntegrity, Limits with { MaximumBytes = 2 },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, body.Reads);
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Theory]
    [InlineData(TransferTimeoutKind.Overall)]
    [InlineData(TransferTimeoutKind.NoProgress)]
    public async Task DownloadAsync_StalledBody_ReportsExpiredBudget(TransferTimeoutKind expectedKind)
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var body = new BlockingReadStream();
        using var client = CreateClient(body);
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(SafetyTimeout);
        var limits = new TransferLimits(
            expectedKind == TransferTimeoutKind.Overall ? TimeSpan.FromMilliseconds(200) : Limits.OverallTimeout,
            expectedKind == TransferTimeoutKind.NoProgress ? TimeSpan.FromMilliseconds(200) : Limits.NoProgressTimeout);

        Task<long> download = ValidatedFileTransfer.DownloadAsync(client, Source, destination, UnknownIntegrity, limits,
            cancellationToken: safety.Token);
        await body.ReadStarted.Task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);
        TransferTimeoutException exception = await Assert.ThrowsAsync<TransferTimeoutException>(() => download);

        Assert.Equal(expectedKind, exception.Kind);
        Assert.False(safety.IsCancellationRequested);
        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_CallerCancelsActiveBody_PreservesCallerTokenAndPublishedArtifact()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var body = new BlockingReadStream();
        using var client = CreateClient(body);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<long> download = ValidatedFileTransfer.DownloadAsync(client, Source, destination, UnknownIntegrity, Limits,
            cancellationToken: cancellation.Token);
        await body.ReadStarted.Task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);

        cancellation.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".msi")]
    public async Task DownloadAsync_ValidatorRejectsClosedStagingFile_PreservesPublishedArtifact(string extension)
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload" + extension);
        File.WriteAllText(destination, "previous-good");
        using var client = CreateClient("abc"u8.ToArray());
        var failure = new InvalidDataException("Validator rejected the artifact.");
        string? stagedPath = null;

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => ValidatedFileTransfer.DownloadAsync(
            client, Source, destination, UnknownIntegrity, Limits,
            (path, token) =>
            {
                stagedPath = path;
                Assert.Equal(extension, Path.GetExtension(path));
                Assert.Equal(workspace.Path, Path.GetDirectoryName(path));
                using var staged = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.Equal(3, staged.Length);
                Assert.Equal("previous-good", File.ReadAllText(destination));
                return Task.FromException(failure);
            }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.NotNull(stagedPath);
        Assert.False(File.Exists(stagedPath));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".msi")]
    public async Task DownloadAsync_StagingRetainsDestinationExtension_ValidatesBeforePublishing(string extension)
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload" + extension);
        File.WriteAllText(destination, "previous-good");
        using var client = CreateClient("abc"u8.ToArray());
        string? stagedPath = null;

        long count = await ValidatedFileTransfer.DownloadAsync(client, Source, destination, UnknownIntegrity, Limits,
            (path, token) =>
            {
                stagedPath = path;
                Assert.Equal(extension, Path.GetExtension(path));
                Assert.Equal(workspace.Path, Path.GetDirectoryName(path));
                Assert.Equal("abc", File.ReadAllText(path));
                Assert.Equal("previous-good", File.ReadAllText(destination));
                return Task.CompletedTask;
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, count);
        Assert.Equal("abc", File.ReadAllText(destination));
        Assert.NotNull(stagedPath);
        Assert.False(File.Exists(stagedPath));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_ConcurrentPublishers_HoldsDestinationLockThroughValidation()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        var validatorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseValidator = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstClient = CreateClient("first"u8.ToArray());
        var secondHandler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("second"u8.ToArray()) });
        using var secondClient = new HttpClient(secondHandler);
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(SafetyTimeout);
        Task<long> first = ValidatedFileTransfer.DownloadAsync(firstClient, Source, destination, UnknownIntegrity, Limits,
            async (_, token) =>
            {
                validatorStarted.TrySetResult();
                await releaseValidator.Task.WaitAsync(token);
            }, cancellationToken: safety.Token);
        Task<long>? second = null;
        try
        {
            await validatorStarted.Task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);
            second = ValidatedFileTransfer.DownloadAsync(secondClient, Source, destination, UnknownIntegrity, Limits,
                cancellationToken: safety.Token);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, secondHandler.Requests);
            Assert.Equal("previous-good", File.ReadAllText(destination));
        }
        finally
        {
            releaseValidator.TrySetResult();
            await first;
            if (second is not null)
            {
                await second;
            }
        }

        Assert.Equal("second", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_HeldLockCanBeCanceled_DoesNotDeleteOtherOwnerLock()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        string lockPath = destination + ".lock";
        using var owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<long> download = ValidatedFileTransfer.DownloadAsync(client, Source, destination, UnknownIntegrity, Limits,
            cancellationToken: cancellation.Token);

        cancellation.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, handler.Requests);
        Assert.True(File.Exists(lockPath));
        Assert.Throws<IOException>(() => File.Delete(lockPath));
    }

    [Fact]
    public async Task DownloadAsync_UnownedExistingLock_DoesNotPreventPublication()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination + ".lock", "stale marker");
        using var client = CreateClient("abc"u8.ToArray());

        Assert.Equal(3, await DownloadAsync(client, destination));

        Assert.Equal("abc", File.ReadAllText(destination));
    }

    [Fact]
    public async Task AcquireDestinationLockAsync_ContentionExpires_RemainsLocalAndPreservesOtherOwner()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        string lockPath = destination + ".lock";
        using var owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(SafetyTimeout);

        await Assert.ThrowsAsync<IOException>(() => ValidatedFileTransfer.AcquireDestinationLockAsync(
            destination, TimeSpan.FromMilliseconds(100), safety.Token));

        Assert.False(safety.IsCancellationRequested);
        Assert.True(File.Exists(lockPath));
        Assert.Throws<IOException>(() => File.Delete(lockPath));
    }

    [Fact]
    public async Task DownloadAsync_ValidatorStalls_UsesOverallBudgetAndPreservesPublishedArtifact()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var client = CreateClient("abc"u8.ToArray());
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(SafetyTimeout);
        var validatorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<long> download = ValidatedFileTransfer.DownloadAsync(client, Source, destination, UnknownIntegrity,
            Limits with { OverallTimeout = TimeSpan.FromMilliseconds(200) },
            async (_, token) =>
            {
                validatorStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, cancellationToken: safety.Token);
        await validatorStarted.Task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);

        TransferTimeoutException exception = await Assert.ThrowsAsync<TransferTimeoutException>(() => download);

        Assert.Equal(TransferTimeoutKind.Overall, exception.Kind);
        Assert.False(safety.IsCancellationRequested);
        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_ValidatorExceedsBodyInactivityBudget_StillPublishes()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        using var client = CreateClient("abc"u8.ToArray());

        long count = await ValidatedFileTransfer.DownloadAsync(client, Source, destination, UnknownIntegrity,
            Limits with { NoProgressTimeout = TimeSpan.FromMilliseconds(200) },
            (_, token) => Task.Delay(TimeSpan.FromMilliseconds(400), token), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, count);
        Assert.Equal("abc", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_HeldDestination_ReportsLocalFailureAndPreservesPublishedArtifact()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var heldFile = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var client = CreateClient("abc"u8.ToArray());

        await Assert.ThrowsAsync<IOException>(() => DownloadAsync(client, destination));

        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_BodyReadFails_ClassifiesTransportAndOnlyCleansOwnedPartial()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        string otherPartial = Path.Combine(workspace.Path, "other.partial.bin");
        File.WriteAllText(destination, "previous-good");
        File.WriteAllText(otherPartial, "another writer");
        var failure = new IOException("Synthetic source read failure.");
        using var body = new FailingReadStream(failure);
        using var client = CreateClient(body);

        TransferReadException exception = await Assert.ThrowsAsync<TransferReadException>(() => DownloadAsync(client, destination));

        Assert.Same(failure, exception.InnerException);
        Assert.DoesNotContain("private", exception.Message, StringComparison.Ordinal);
        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Equal([otherPartial], Directory.GetFiles(workspace.Path, "*.partial*"));
        Assert.Equal("another writer", File.ReadAllText(otherPartial));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_HttpFailure_RetainsRetryGuidanceWithoutReadingBody(bool useDelta)
    {
        using var workspace = new TemporaryDirectory();
        using var body = new ChunkedStream("sensitive body"u8.ToArray(), 1);
        DateTimeOffset retryDate = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var client = new HttpClient(new ResponseHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                ReasonPhrase = "private reason",
                Content = new StreamContent(body)
            };
            response.Headers.RetryAfter = useDelta ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(12)) : new RetryConditionHeaderValue(retryDate);
            return response;
        }));

        HttpResponseException exception = await Assert.ThrowsAsync<HttpResponseException>(() => DownloadAsync(client, Path.Combine(workspace.Path, "payload.bin")));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(useDelta ? TimeSpan.FromSeconds(12) : null, exception.RetryAfter);
        Assert.Equal(useDelta ? null : retryDate, exception.RetryAfterDate);
        Assert.DoesNotContain("private", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, body.Reads);
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task CopyBodyAsync_LocalWriteFails_DoesNotClassifyAsTransportFailure()
    {
        using var source = new MemoryStream("abc"u8.ToArray());
        using var content = new StreamContent(source);
        var failure = new IOException("Synthetic local write failure.");
        using var destination = new FailingWriteStream(failure);

        IOException exception = await Assert.ThrowsAsync<IOException>(() => ValidatedFileTransfer.CopyBodyAsync(
            content, destination, UnknownIntegrity, Limits, null, null, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task DownloadAsync_BodyStreamAcquisitionStalls_UsesNoProgressBudget()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var content = new BlockingStreamContent();
        using var client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(SafetyTimeout);
        Task<long> download = ValidatedFileTransfer.DownloadAsync(client, Source, destination, UnknownIntegrity,
            Limits with { NoProgressTimeout = TimeSpan.FromMilliseconds(200) }, cancellationToken: safety.Token);
        await content.OpenStarted.Task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);

        TransferTimeoutException exception = await Assert.ThrowsAsync<TransferTimeoutException>(() => download);

        Assert.Equal(TransferTimeoutKind.NoProgress, exception.Kind);
        Assert.False(safety.IsCancellationRequested);
        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_BodyAndDisposalFail_PreservesPrimaryReadFailure()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        var failure = new IOException("Synthetic source failure.");
        var body = new FailingReadAndDisposeStream(failure);
        using var client = CreateClient(body);

        TransferReadException exception = await Assert.ThrowsAsync<TransferReadException>(() => DownloadAsync(client, destination));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task DownloadAsync_BodyDisposalFails_DoesNotPublishReplacement()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        File.WriteAllText(destination, "previous-good");
        using var client = CreateClient(new FailingReadAndDisposeStream(null));

        await Assert.ThrowsAsync<IOException>(() => DownloadAsync(client, destination));

        Assert.Equal("previous-good", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.partial*"));
    }

    [Fact]
    public async Task CopyBodyAsync_BlockedWrite_ExpiresWithoutReportingProgress()
    {
        using var source = new MemoryStream("abc"u8.ToArray());
        using var content = new StreamContent(source);
        using var destination = new BlockingWriteStream();
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(SafetyTimeout);
        var progress = new List<long>();
        Task<long> copy = ValidatedFileTransfer.CopyBodyAsync(content, destination, UnknownIntegrity,
            Limits with { NoProgressTimeout = TimeSpan.FromMilliseconds(200) }, null, new InlineProgress(progress.Add), safety.Token);
        await destination.WriteStarted.Task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);

        TransferTimeoutException exception = await Assert.ThrowsAsync<TransferTimeoutException>(() => copy);

        Assert.Equal(TransferTimeoutKind.NoProgress, exception.Kind);
        Assert.False(safety.IsCancellationRequested);
        Assert.Empty(progress);
    }

    [Fact]
    public async Task CopyBodyAsync_LocalFlushFails_DoesNotClassifyAsTransportFailure()
    {
        using var source = new MemoryStream("abc"u8.ToArray());
        using var content = new StreamContent(source);
        var failure = new IOException("Synthetic local flush failure.");
        using var destination = new FailingFlushStream(failure);

        IOException exception = await Assert.ThrowsAsync<IOException>(() => ValidatedFileTransfer.CopyBodyAsync(
            content, destination, UnknownIntegrity, Limits, null, null, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task CopyBodyAsync_CallerCancelsBlockedWrite_PreservesTokenWithoutReportingProgress()
    {
        using var source = new MemoryStream("abc"u8.ToArray());
        using var content = new StreamContent(source);
        using var destination = new BlockingWriteStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new List<long>();
        Task<long> copy = ValidatedFileTransfer.CopyBodyAsync(content, destination, UnknownIntegrity, Limits, null,
            new InlineProgress(progress.Add), cancellation.Token);
        await destination.WriteStarted.Task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);

        cancellation.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(progress);
    }

    [Fact]
    public async Task DownloadAsync_ChunkedContent_ReportsWrittenBytesWithBoundedReads()
    {
        using var workspace = new TemporaryDirectory();
        string destination = Path.Combine(workspace.Path, "payload.bin");
        using var body = new ChunkedStream(new byte[200_000], int.MaxValue);
        using var client = CreateClient(body);
        var progress = new List<long>();

        long count = await ValidatedFileTransfer.DownloadAsync(client, Source, destination, new FileIntegrity(null, 200_000), Limits,
            progress: new InlineProgress(progress.Add), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(200_000, count);
        Assert.Equal([81_920L, 163_840L, 200_000L], progress);
        Assert.InRange(body.MaximumReadBuffer, 1, 81_920);
        Assert.Equal(200_000, new FileInfo(destination).Length);
    }

    private static Task<long> DownloadAsync(HttpClient client, string destination, FileIntegrity? integrity = null)
    {
        return ValidatedFileTransfer.DownloadAsync(client, Source, destination, integrity ?? UnknownIntegrity, Limits,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static HttpClient CreateClient(byte[] bytes)
    {
        return new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        }));
    }

    private static HttpClient CreateClient(Stream body, long? responseLength = null)
    {
        return new HttpClient(new ResponseHandler(() =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = responseLength;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkedStream(byte[] bytes, int chunkSize) : ReadOnlyStream
    {
        private int _position;
        public int Reads { get; private set; }
        public int MaximumReadBuffer { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            MaximumReadBuffer = Math.Max(MaximumReadBuffer, buffer.Length);
            int length = Math.Min(Math.Min(chunkSize, buffer.Length), bytes.Length - _position);
            bytes.AsMemory(_position, length).CopyTo(buffer);
            _position += length;
            return ValueTask.FromResult(length);
        }
    }

    private sealed class BlockingReadStream : ReadOnlyStream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FailingReadStream(Exception exception) : ReadOnlyStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<int>(exception);
        }
    }

    private sealed class FailingWriteStream(Exception exception) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException(exception);
        }
    }

    private sealed class FailingFlushStream(Exception exception) : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.FromException(exception);
        }
    }

    private sealed class FailingReadAndDisposeStream(Exception? readFailure) : ReadOnlyStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return readFailure is null ? ValueTask.FromResult(0) : ValueTask.FromException<int>(readFailure);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException("Synthetic disposal failure.");
        }
    }

    private sealed class BlockingStreamContent : HttpContent
    {
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            OpenStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Stream.Null;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            throw new InvalidOperationException("The transfer must request response headers without buffering content.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BlockingWriteStream : MemoryStream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(response());
        }
    }
}
