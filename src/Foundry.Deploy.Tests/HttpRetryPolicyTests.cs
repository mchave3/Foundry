// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;
using Foundry.Deploy.Services.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class HttpRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_LocalStorageFailure_IsNotRetried()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => HttpRetryPolicy.ExecuteAsync(
            _ =>
            {
                attempts++;
                throw new IOException("Local write failed.");
            },
            NullLogger.Instance,
            "artifact write",
            TestContext.Current.CancellationToken,
            options: HttpOperationOptions.Metadata with { InitialRetryDelay = TimeSpan.Zero }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_TlsFailure_IsNotRetried()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => HttpRetryPolicy.ExecuteAsync(
            _ =>
            {
                attempts++;
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS validation failed.");
            },
            NullLogger.Instance,
            "catalog request",
            TestContext.Current.CancellationToken,
            options: HttpOperationOptions.Metadata with { InitialRetryDelay = TimeSpan.Zero }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFailureIsTransient_RetriesUntilSuccess()
    {
        int attempts = 0;

        string result = await HttpRetryPolicy.ExecuteAsync(
            async _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new HttpRequestException("Transient failure", null, HttpStatusCode.ServiceUnavailable);
                }

                await Task.CompletedTask;
                return "ok";
            },
            NullLogger.Instance,
            "download catalog",
            TestContext.Current.CancellationToken,
            options: HttpOperationOptions.Metadata with { MaximumAttempts = 4, InitialRetryDelay = TimeSpan.Zero });

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFailureIsNotRetryable_DoesNotRetry()
    {
        int attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            HttpRetryPolicy.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    throw new HttpRequestException("Not found", null, HttpStatusCode.NotFound);
                },
                NullLogger.Instance,
                "download catalog",
                TestContext.Current.CancellationToken,
                options: HttpOperationOptions.Metadata with { MaximumAttempts = 4, InitialRetryDelay = TimeSpan.Zero }));

        Assert.Equal(1, attempts);
    }
}
