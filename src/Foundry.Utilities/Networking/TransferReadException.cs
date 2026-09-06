// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Utilities.Networking;

/// <summary>
/// Distinguishes response-body read failures from local storage and validation failures for retry policy.
/// </summary>
public sealed class TransferReadException(Exception innerException)
    : IOException("The transfer response body could not be read.", innerException);
