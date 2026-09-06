// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Utilities.Networking;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Http;

/// <summary>Applies Deploy HTTP budgets and safe retry diagnostics to the shared transport policy.</summary>
public static class HttpRetryPolicy
{
    public const int DefaultRetryCount = 2;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    public static Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default,
        HttpRetryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        return ExecuteAsync(async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        }, logger, operationName, cancellationToken, options);
    }

    public static Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default,
        HttpRetryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        return HttpRetry.ExecuteAsync(action, options ?? HttpOperationOptions.Metadata,
            cancellationToken, (attempt, delay, error) => logger.LogWarning(
                "HTTP operation {OperationName} failed on attempt {Attempt}. ErrorType={ErrorType}; retrying in {DelaySeconds} seconds.",
                operationName, attempt, error.GetType().Name, delay.TotalSeconds));
    }
}
