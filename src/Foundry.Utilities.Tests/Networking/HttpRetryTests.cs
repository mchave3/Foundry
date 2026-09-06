// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;
using Foundry.Utilities.Networking;

namespace Foundry.Utilities.Tests.Networking;

public sealed class HttpRetryTests
{
    private static readonly HttpRetryOptions Options = new(3, TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RespectsRetryAfterDeltaAndDate(bool dateHeader)
    {
        var clock = new TestClock();
        var waits = new List<TimeSpan>();
        int attempts = 0;
        string result = await HttpRetry.ExecuteAsync<string>(
            _ => ++attempts == 1
                ? throw new HttpResponseException(HttpStatusCode.TooManyRequests,
                    dateHeader ? null : TimeSpan.FromSeconds(12),
                    dateHeader ? clock.GetUtcNow().AddSeconds(12) : null)
                : Task.FromResult("complete"),
            Options, clock, (wait, token) =>
            {
                token.ThrowIfCancellationRequested();
                waits.Add(wait);
                clock.Advance(wait);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

        Assert.Equal("complete", result);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromSeconds(12)], waits);
    }

    [Fact]
    public async Task ExecuteAsync_ServerMinimumExceedsBudget_DoesNotRetryEarly()
    {
        int attempts = 0;
        bool delayed = false;
        TransferTimeoutException error = await Assert.ThrowsAsync<TransferTimeoutException>(() =>
            HttpRetry.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new HttpResponseException(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(2));
            }, Options, new TestClock(), (_, _) =>
            {
                delayed = true;
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken));

        Assert.Equal(TransferTimeoutKind.Overall, error.Kind);
        Assert.Equal(1, attempts);
        Assert.False(delayed);
    }

    [Fact]
    public async Task ExecuteAsync_Exhaustion_UsesBoundedExponentialBackoff()
    {
        var waits = new List<TimeSpan>();
        var clock = new TestClock();
        int attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => HttpRetry.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Disconnected.");
        }, Options, clock, (wait, _) =>
        {
            waits.Add(wait);
            clock.Advance(wait);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken));

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], waits);
    }

    [Fact]
    public async Task ExecuteAsync_CancelDuringBackoff_PreservesCallerToken()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int attempts = 0;
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpRetry.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new HttpResponseException(HttpStatusCode.ServiceUnavailable);
            }, Options, new TestClock(), (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_StalledAttempt_ReportsTimeoutAfterBoundedRetries()
    {
        int attempts = 0;
        HttpRetryOptions limits = Options with
        {
            MaximumAttempts = 2, RequestTimeout = TimeSpan.FromMilliseconds(30), InitialRetryDelay = TimeSpan.Zero
        };
        await Assert.ThrowsAnyAsync<TimeoutException>(() => HttpRetry.ExecuteAsync<int>(async token =>
        {
            attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }, limits, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_LocalWriteAndTlsFailures_AreNotRetried(bool tls)
    {
        int attempts = 0;
        Exception expected = tls
            ? new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS rejected.")
            : new IOException("Local write failed.");
        Exception? actual = await Record.ExceptionAsync(() => HttpRetry.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw expected;
        }, Options, TestContext.Current.CancellationToken));
        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_CompletionAfterOverallBudget_IsNotReportedAsSuccess()
    {
        var clock = new TestClock();
        int attempts = 0;
        TransferTimeoutException error = await Assert.ThrowsAsync<TransferTimeoutException>(() =>
            HttpRetry.ExecuteAsync<int>(_ =>
            {
                attempts++;
                clock.Advance(TimeSpan.FromSeconds(61));
                return Task.FromResult(7);
            }, Options, clock, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(TransferTimeoutKind.Overall, error.Kind);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_TypedTlsFailureInsideReadFailure_IsNotRetried()
    {
        int attempts = 0;
        var expected = new TransferReadException(new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS rejected."));
        Exception? actual = await Record.ExceptionAsync(() => HttpRetry.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw expected;
        }, Options with { InitialRetryDelay = TimeSpan.Zero }, TestContext.Current.CancellationToken));
        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }
}
