// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net.Http;
using Foundry.Utilities.Networking;

namespace Foundry.Deploy.Services.Http;

/// <summary>Uses OS certificate trust and validates destinations before following acquisition redirects.</summary>
public static class AcquisitionHttpClientFactory
{
    public static HttpClient Create(TimeSpan timeout) => Create(timeout, RequireHttps);

    public static HttpClient Create(TimeSpan timeout, Action<Uri> validateUri)
    {
        ArgumentNullException.ThrowIfNull(validateUri);
        var transport = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        return new HttpClient(new ValidatedRedirectHandler(transport, validateUri)) { Timeout = timeout };
    }

    internal static void RequireHttps(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Catalog acquisition requires an authenticated HTTPS address without embedded credentials.");
        }
    }
}
