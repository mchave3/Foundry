// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace Foundry.Utilities.Networking;

/// <summary>
/// Retains HTTP status and retry guidance without retaining the response body or source address.
/// </summary>
public sealed class HttpResponseException(
    HttpStatusCode statusCode,
    TimeSpan? retryAfter = null,
    DateTimeOffset? retryAfterDate = null)
    : HttpRequestException("The transfer request returned an unsuccessful HTTP status.", null, statusCode)
{
    /// <summary>
    /// Gets the server's relative Retry-After delay, when supplied.
    /// </summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>
    /// Gets the server's absolute Retry-After time, when supplied.
    /// </summary>
    public DateTimeOffset? RetryAfterDate { get; } = retryAfterDate;
}
