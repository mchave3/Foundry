// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;
using Foundry.Deploy.Services.Http;
using Foundry.Utilities.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class HttpTextFetcherTests
{
    private static readonly HttpRetryOptions FastOptions = HttpOperationOptions.Metadata with
    {
        MaximumAttempts = 1,
        RequestTimeout = TimeSpan.FromMilliseconds(50)
    };

    [Fact]
    public async Task GetStringAsync_OversizedDeclaredMetadata_IsRejectedWithoutRetry()
    {
        using var handler = new CallbackHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
            response.Content.Headers.ContentLength = HttpTextFetcher.MaximumMetadataBytes + 1;
            return response;
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(client, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetStringAsync_BodyStallsAfterHeaders_StillHasDeadline()
    {
        using var handler = new CallbackHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream())
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<TimeoutException>(() => FetchAsync(client,
            TestContext.Current.CancellationToken, FastOptions).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetStringAsync_CancelDuringBody_PreservesCallerCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CallbackHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream(entered))
        });
        using var client = new HttpClient(handler);
        Task<string> reading = FetchAsync(client, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
        }

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SendStringAsync_Retry_CreatesFreshPostRequest()
    {
        var requests = new List<HttpRequestMessage>();
        using var handler = new CallbackHandler(() => requests.Count == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("complete") });
        using var client = new HttpClient(handler);
        string result = await HttpTextFetcher.SendStringWithRetryAsync(client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/catalog")
            {
                Content = new FormUrlEncodedContent([new("id", "fixture")])
            };
            requests.Add(request);
            return request;
        }, NullLogger.Instance, "catalog", TestContext.Current.CancellationToken,
            HttpOperationOptions.Metadata with { InitialRetryDelay = TimeSpan.Zero });

        Assert.Equal("complete", result);
        Assert.Equal(2, requests.Count);
        Assert.NotSame(requests[0], requests[1]);
    }

    [Fact]
    public async Task GetStringAsync_HttpMetadata_IsRejectedBeforeNetwork()
    {
        using var handler = new CallbackHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => HttpTextFetcher.GetStringWithRetryAsync(
            client, "http://example.test/catalog", NullLogger.Instance, "catalog", TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetStringAsync_RetryableStatusAndCleanupFail_PreservesRetry()
    {
        int attempts = 0;
        using var handler = new CallbackHandler(() => ++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new FailingDisposeContent() }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("complete") });
        using var client = new HttpClient(handler);
        string result = await FetchAsync(client, TestContext.Current.CancellationToken,
            HttpOperationOptions.Metadata with { InitialRetryDelay = TimeSpan.Zero });
        Assert.Equal("complete", result);
        Assert.Equal(2, attempts);
    }

    private sealed class FailingDisposeContent : ByteArrayContent
    {
        public FailingDisposeContent() : base([]) { }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException("Secondary response disposal failure.");
        }
    }

    private static Task<string> FetchAsync(HttpClient client, CancellationToken token, HttpRetryOptions? options = null)
        => HttpTextFetcher.GetStringWithRetryAsync(client, "https://example.test/catalog",
            NullLogger.Instance, "catalog", token, options);

    private sealed class CallbackHandler(Func<HttpResponseMessage> callback) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(callback());
        }
    }

    private sealed class StalledStream(TaskCompletionSource? entered = null) : MemoryStream
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered?.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
