// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Utilities.Networking;

/// <summary>
/// Supplies expected file bytes; null values mean unknown, and callers own the source's trust policy.
/// </summary>
public sealed record FileIntegrity(FileDigest? Digest, long? SizeBytes);
