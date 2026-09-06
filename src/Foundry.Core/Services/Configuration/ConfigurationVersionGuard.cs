// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace Foundry.Core.Services.Configuration;

/// <summary>
/// Reads only schema metadata so unsupported documents are rejected before their future contents are interpreted.
/// </summary>
public static class ConfigurationVersionGuard
{
    public static void ThrowIfUnsupported(
        string json,
        string contract,
        int supportedVersion,
        JsonSerializerOptions serializerOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentNullException.ThrowIfNull(serializerOptions);

        ConfigurationVersionEnvelope? envelope = JsonSerializer.Deserialize<ConfigurationVersionEnvelope>(
            json,
            serializerOptions);
        if (envelope?.SchemaVersion > supportedVersion)
        {
            throw new UnsupportedConfigurationVersionException(
                contract,
                envelope.SchemaVersion,
                supportedVersion);
        }
    }

    private sealed class ConfigurationVersionEnvelope
    {
        public int SchemaVersion { get; init; }
    }
}
