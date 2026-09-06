// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;

namespace Foundry.Utilities.Networking;

/// <summary>
/// Identifies an explicitly selected hash algorithm and its expected hexadecimal digest.
/// </summary>
public sealed record FileDigest(HashAlgorithmName Algorithm, string Hex);
