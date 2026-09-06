// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.Configuration;

/// <summary>
/// Reports that a configuration document requires a newer schema contract.
/// </summary>
public sealed class UnsupportedConfigurationVersionException : Exception
{
    /// <summary>
    /// Initializes an unsupported configuration version failure.
    /// </summary>
    public UnsupportedConfigurationVersionException(string contract, int actualVersion, int supportedVersion)
        : base($"{contract} configuration uses schema version {actualVersion}, but this application supports up to schema version {supportedVersion}.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        Contract = contract;
        ActualVersion = actualVersion;
        SupportedVersion = supportedVersion;
    }

    public string Contract { get; }

    public int ActualVersion { get; }

    public int SupportedVersion { get; }
}
