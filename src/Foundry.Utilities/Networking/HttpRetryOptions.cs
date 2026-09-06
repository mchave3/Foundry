// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Utilities.Networking;

/// <summary>Bounds an HTTP operation, including all attempts and server-directed retry delays.</summary>
public sealed record HttpRetryOptions(
    int MaximumAttempts,
    TimeSpan OverallTimeout,
    TimeSpan RequestTimeout,
    TimeSpan InitialRetryDelay,
    TimeSpan MaximumRetryDelay);
