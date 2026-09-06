// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Utilities.Networking;

/// <summary>
/// Identifies the internal transfer budget that expired, independently of caller cancellation.
/// </summary>
public enum TransferTimeoutKind
{
    /// <summary>
    /// The complete operation, including lock acquisition and staged validation, exhausted its budget.
    /// </summary>
    Overall,

    /// <summary>
    /// Body acquisition, copying or flushing stopped making progress.
    /// </summary>
    NoProgress
}

/// <summary>
/// Reports an expired transfer budget without exposing the source address or response body.
/// </summary>
public sealed class TransferTimeoutException(TransferTimeoutKind kind)
    : TimeoutException(kind == TransferTimeoutKind.Overall
        ? "The transfer exceeded its overall time limit."
        : "The transfer exceeded its no-progress time limit.")
{
    /// <summary>
    /// Gets the internal budget that ended the transfer.
    /// </summary>
    public TransferTimeoutKind Kind { get; } = kind;
}
