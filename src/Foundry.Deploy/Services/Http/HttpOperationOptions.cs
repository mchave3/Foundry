// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Utilities.Networking;

namespace Foundry.Deploy.Services.Http;

/// <summary>Defines Deploy budgets for catalog requests and streamed deployment payloads.</summary>
public static class HttpOperationOptions
{
    public static HttpRetryOptions Metadata { get; } = new(3, TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    public static HttpRetryOptions Payload { get; } = new(3, TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
}
