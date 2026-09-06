// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Foundry.Core.Models.Configuration;

namespace Foundry.Core.Services.Configuration;

public sealed class FoundryConfigurationService : IFoundryConfigurationService
{
    public string Serialize(FoundryConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, ConfigurationJsonDefaults.SerializerOptions);
    }

    public FoundryConfigurationDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ConfigurationVersionGuard.ThrowIfUnsupported(
            json,
            "Foundry",
            ConfigurationSchemaVersions.FoundryCurrent,
            ConfigurationJsonDefaults.SerializerOptions);
        FoundryConfigurationDocument document = JsonSerializer.Deserialize<FoundryConfigurationDocument>(
                json,
                ConfigurationJsonDefaults.SerializerOptions)
            ?? new FoundryConfigurationDocument();
        if (document.SchemaVersion > ConfigurationSchemaVersions.FoundryCurrent)
        {
            throw new UnsupportedConfigurationVersionException(
                "Foundry",
                document.SchemaVersion,
                ConfigurationSchemaVersions.FoundryCurrent);
        }

        FoundryConfigurationDocument normalized = FoundryConfigurationNormalizer.Normalize(document);
        return FoundryConfigurationMigration.ApplySchemaMigrations(normalized);
    }
}
