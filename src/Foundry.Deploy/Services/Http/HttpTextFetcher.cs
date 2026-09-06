// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net.Http;
using System.Runtime.ExceptionServices;
using Foundry.Utilities.Networking;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Http;

/// <summary>Acquires bounded HTTPS metadata within one deadline covering headers, body and retries.</summary>
public static class HttpTextFetcher
{
    public const long MaximumMetadataBytes = 32L * 1024 * 1024;

    public static Task<string> GetStringWithRetryAsync(
        HttpClient client,
        string requestUri,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default,
        HttpRetryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUri);
        return SendStringWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Get, requestUri),
            logger, operationName, cancellationToken, options);
    }

    public static Task<string> SendStringWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default,
        HttpRetryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        return HttpRetryPolicy.ExecuteAsync(async token =>
        {
            HttpRequestMessage? request = null;
            HttpResponseMessage? response = null;
            Exception? failure = null;
            try
            {
                request = requestFactory();
                AcquisitionHttpClientFactory.RequireHttps(request.RequestUri);
                response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                AcquisitionHttpClientFactory.RequireHttps(response.RequestMessage?.RequestUri ?? request.RequestUri);
                return await ReadBoundedStringAsync(response, token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                Exception? cleanupFailure = null;
                DisposeOwned(response, ref cleanupFailure);
                DisposeOwned(request, ref cleanupFailure);
                if (failure is null && cleanupFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                }
            }
        }, logger, operationName, cancellationToken, options);
    }

    public static Task<string> ReadBoundedStringAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => BoundedHttpContent.ReadStringAsync(response, MaximumMetadataBytes, cancellationToken);

    private static void DisposeOwned(IDisposable? resource, ref Exception? firstFailure)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception error)
        {
            firstFailure ??= error;
        }
    }
}
