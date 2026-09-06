// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.Frozen;

namespace Foundry.Core.Services.Security;

/// <summary>Limits executable trust to publishers observed in official driver package families.</summary>
public static class VendorExecutableTrustPolicy
{
    private static readonly FrozenSet<string> DellSubjects = new[]
    {
        "CN=Dell Technologies Inc., OU=DUP Client Creation Service, O=Dell Technologies Inc., L=Round Rock, S=Texas, C=US"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> LenovoSubjects = new[]
    {
        "CN=Lenovo, OU=G10, O=Lenovo, L=Morrisville, S=North Carolina, C=US"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SurfaceSubjects = new[]
    {
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> HpWinPeSubjects = new[]
    {
        "CN=HP Inc., O=HP Inc., L=Palo Alto, S=California, C=US"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns exact subjects qualified for the named family; unknown families fail closed.</summary>
    /// <remarks>Subjects were read from authenticated official driver package signature tables on 2026-09-06; runtime Windows trust verification remains required.</remarks>
    public static IReadOnlySet<string> GetExpectedPublisherSubjects(string packageFamily) => packageFamily switch
    {
        "DellDriverPack" => DellSubjects,
        "LenovoDriverPack" => LenovoSubjects,
        "SurfaceDriverPack" => SurfaceSubjects,
        "HpWinPeDriverPack" => HpWinPeSubjects,
        _ => throw new InvalidDataException($"Trusted publisher policy is unavailable for '{packageFamily}'. Qualify an official signed package from this family before execution.")
    };

    /// <summary>Restricts signature-only fresh acquisitions to the family's official HTTPS delivery host.</summary>
    public static void ValidateDownloadSource(string packageFamily, Uri source)
    {
        string host = packageFamily switch
        {
            "DellDriverPack" => "downloads.dell.com",
            "LenovoDriverPack" => "download.lenovo.com",
            "SurfaceDriverPack" => "download.microsoft.com",
            "HpWinPeDriverPack" => "ftp.ext.hp.com",
            _ => throw new InvalidDataException($"Trusted publisher policy is unavailable for '{packageFamily}'.")
        };
        if (!source.IsAbsoluteUri || source.Scheme != Uri.UriSchemeHttps ||
            !source.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || !source.IsDefaultPort ||
            !string.IsNullOrEmpty(source.UserInfo))
        {
            throw new InvalidDataException("The driver package source does not match its trusted publisher family.");
        }
    }
}
