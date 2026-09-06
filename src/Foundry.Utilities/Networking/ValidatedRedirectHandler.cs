// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace Foundry.Utilities.Networking;

/// <summary>Checks every request destination before sending it; the inner handler must disable automatic redirects.</summary>
public sealed class ValidatedRedirectHandler(HttpMessageHandler innerHandler, Action<Uri> validateUri)
    : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validateUri);
        HttpRequestMessage current = request;
        try
        {
            for (int hop = 0; ; hop++)
            {
                Uri uri = current.RequestUri ?? throw new InvalidOperationException("HTTP request URI is required.");
                validateUri(uri);
                HttpResponseMessage response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                {
                    return response;
                }

                using (response)
                {
                    if (hop >= 5 || response.Headers.Location is not { } location ||
                        (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head))
                    {
                        throw new HttpRequestException("The HTTP redirect cannot be followed safely.");
                    }

                    Uri nextUri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    validateUri(nextUri);
                    var next = new HttpRequestMessage(request.Method, nextUri);
                    foreach (var header in current.Headers)
                    {
                        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                            header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                            header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                            header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (!ReferenceEquals(current, request))
                    {
                        current.Dispose();
                    }

                    current = next;
                }
            }
        }
        finally
        {
            if (!ReferenceEquals(current, request))
            {
                current.Dispose();
            }
        }
    }
}
