// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Security.Authentication;

namespace Foundry.Utilities.Networking;

/// <summary>Retries identified HTTP failures without retrying local storage or integrity failures.</summary>
public static class HttpRetry
{
    public static Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        HttpRetryOptions options,
        CancellationToken cancellationToken = default,
        Action<int, TimeSpan, Exception>? onRetry = null)
        => ExecuteAsync(action, options, TimeProvider.System,
            (delay, token) => Task.Delay(delay, token), cancellationToken, onRetry);

    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        HttpRetryOptions options,
        TimeProvider clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken = default,
        Action<int, TimeSpan, Exception>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        long started = clock.GetTimestamp();
        using var overall = new CancellationTokenSource(options.OverallTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overall.Token);

        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (overall.IsCancellationRequested || clock.GetElapsedTime(started) >= options.OverallTimeout)
            {
                throw new TransferTimeoutException(TransferTimeoutKind.Overall);
            }

            Exception failure;
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(operation.Token))
            {
                request.CancelAfter(options.RequestTimeout);
                try
                {
                    T result = await action(request.Token).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (overall.IsCancellationRequested || clock.GetElapsedTime(started) >= options.OverallTimeout)
                    {
                        throw new TransferTimeoutException(TransferTimeoutKind.Overall);
                    }

                    request.Token.ThrowIfCancellationRequested();
                    return result;
                }
                catch (Exception error)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (overall.IsCancellationRequested)
                    {
                        throw new TransferTimeoutException(TransferTimeoutKind.Overall);
                    }

                    failure = error is OperationCanceledException &&
                        (request.IsCancellationRequested || error.InnerException is TimeoutException)
                            ? new HttpAttemptTimeoutException(error)
                            : error;
                    if (!IsRetryable(failure) || attempt >= options.MaximumAttempts)
                    {
                        if (ReferenceEquals(failure, error))
                        {
                            throw;
                        }

                        throw failure;
                    }
                }
            }

            TimeSpan backoff = TimeSpan.FromTicks((long)Math.Min(
                options.MaximumRetryDelay.Ticks,
                options.InitialRetryDelay.Ticks * Math.Pow(2, attempt - 1)));
            TimeSpan serverDelay = GetRetryAfter(failure, clock.GetUtcNow());
            TimeSpan wait = serverDelay > backoff ? serverDelay : backoff;
            TimeSpan remaining = options.OverallTimeout - clock.GetElapsedTime(started);
            if (wait >= remaining)
            {
                throw new TransferTimeoutException(TransferTimeoutKind.Overall);
            }

            onRetry?.Invoke(attempt, wait, failure);
            try
            {
                await delay(wait, operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (overall.IsCancellationRequested)
                {
                    throw new TransferTimeoutException(TransferTimeoutKind.Overall);
                }

                throw;
            }
        }
    }

    private static bool IsRetryable(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is AuthenticationException or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
            {
                return false;
            }
        }

        return error switch
        {
            HttpAttemptTimeoutException or TransferReadException => true,
            TransferTimeoutException { Kind: TransferTimeoutKind.NoProgress } => true,
            HttpRequestException { StatusCode: { } status } => status is
                HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
                HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
            HttpRequestException request => request.HttpRequestError is
                HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or
                HttpRequestError.HttpProtocolError or HttpRequestError.ResponseEnded,
            _ => false
        };
    }

    private static TimeSpan GetRetryAfter(Exception error, DateTimeOffset now)
    {
        if (error is not HttpResponseException response)
        {
            return TimeSpan.Zero;
        }

        TimeSpan value = response.RetryAfter ?? (response.RetryAfterDate - now) ?? TimeSpan.Zero;
        return value > TimeSpan.Zero ? value : TimeSpan.Zero;
    }

    private static void ValidateOptions(HttpRetryOptions options)
    {
        TimeSpan maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (options.MaximumAttempts is < 1 or > 100 ||
            options.OverallTimeout <= TimeSpan.Zero || options.OverallTimeout > maximum ||
            options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > maximum ||
            options.InitialRetryDelay < TimeSpan.Zero || options.MaximumRetryDelay < options.InitialRetryDelay ||
            options.MaximumRetryDelay > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "HTTP limits must define finite positive deadlines and bounded attempts and delays.");
        }
    }

    private sealed class HttpAttemptTimeoutException(Exception innerException)
        : TimeoutException("The HTTP request exceeded its deadline.", innerException);
}
