// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using Foundry.Utilities.Networking;

namespace Foundry.Utilities.Tests.Networking;

public sealed class HttpAcquisitionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadStringAsync_RejectsDeclaredAndChunkedOversizedMetadata(bool declared)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = declared
                ? new ByteArrayContent(new byte[33])
                : new StreamContent(new NonSeekableStream(new byte[33]))
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => BoundedHttpContent.ReadStringAsync(
            response, 32, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadStringAsync_PreservesDeclaredEncoding()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("café", System.Text.Encoding.Unicode)
        };
        Assert.Equal("café", await BoundedHttpContent.ReadStringAsync(
            response, 32, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadStringAsync_PreservesServerRetryAfter()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        HttpResponseException error = await Assert.ThrowsAsync<HttpResponseException>(() =>
            BoundedHttpContent.ReadStringAsync(response, 32, TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromSeconds(12), error.RetryAfter);
    }

    [Fact]
    public async Task Redirect_HttpsDowngrade_IsRejectedBeforeSendingNextRequest()
    {
        int sent = 0;
        using var client = new HttpClient(new ValidatedRedirectHandler(new CallbackHandler(_ =>
        {
            sent++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://cdn.example.test/catalog");
            return response;
        }), RequireHttps));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(
            "https://example.test/catalog", TestContext.Current.CancellationToken));
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task Redirect_ValidHttpsHop_DropsCredentialsAndPreservesSafeHeaders()
    {
        var seen = new List<Uri>();
        using var client = new HttpClient(new ValidatedRedirectHandler(new CallbackHandler(request =>
        {
            seen.Add(request.RequestUri!);
            if (seen.Count == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://cdn.example.test/catalog");
                return response;
            }

            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("application/xml", request.Headers.Accept.Single().MediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("complete") };
        }), RequireHttps));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/catalog");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-only");
        request.Headers.Add("Cookie", "session=test-only");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        using HttpResponseMessage result = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("complete", await result.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task Redirect_Loop_StopsAfterFiveHops()
    {
        int sent = 0;
        using var client = new HttpClient(new ValidatedRedirectHandler(new CallbackHandler(_ =>
        {
            sent++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("/loop", UriKind.Relative);
            return response;
        }), RequireHttps));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(
            "https://example.test/loop", TestContext.Current.CancellationToken));
        Assert.Equal(6, sent);
    }

    [Fact]
    public async Task ReadStringAsync_StreamOpenFailure_RemainsTransportFailure()
    {
        var expected = new IOException("Remote stream unavailable.");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingOpenContent(expected) };
        TransferReadException error = await Assert.ThrowsAsync<TransferReadException>(() =>
            BoundedHttpContent.ReadStringAsync(response, 32, TestContext.Current.CancellationToken));
        Assert.Same(expected, error.InnerException);
    }

    [Fact]
    public async Task ReadStringAsync_ReadAndCleanupFail_PreservesReadFailure()
    {
        var expected = new IOException("Remote body failed.");
        var stream = new FailingReadAndDisposeStream(expected);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        try
        {
            TransferReadException error = await Assert.ThrowsAsync<TransferReadException>(() =>
                BoundedHttpContent.ReadStringAsync(response, 32, TestContext.Current.CancellationToken));
            Assert.Same(expected, error.InnerException);
        }
        finally
        {
            stream.FailDispose = false;
            response.Dispose();
        }
    }

    private sealed class FailingOpenContent(IOException error) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromException<Stream>(error);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.FromException(error);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class FailingReadAndDisposeStream(IOException error) : MemoryStream
    {
        public bool FailDispose { get; set; } = true;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(error);
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (FailDispose) { throw new IOException("Secondary cleanup failure."); }
        }
    }

    private static void RequireHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("HTTPS is required.");
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
