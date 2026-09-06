// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Utilities.Networking;

/// <summary>
/// Bounds the complete operation and body inactivity, including stream acquisition and flushing.
/// Successful writes reset inactivity; staged validation uses only the overall budget.
/// </summary>
public sealed record TransferLimits(TimeSpan OverallTimeout, TimeSpan NoProgressTimeout, long? MaximumBytes = null);
