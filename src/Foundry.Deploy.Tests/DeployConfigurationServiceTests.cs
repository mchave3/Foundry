// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class DeployConfigurationServiceTests
{
    [Theory]
    [InlineData("{\"isEnabled\":true,\"files\":[]}")]
    [InlineData("{\"isEnabled\":true,\"defaultFileId\":\"missing\",\"files\":[]}")]
    public void LoadOptional_WhenUnattendManifestIsInvalid_FailsClosed(string manifest)
    {
        using var tempDirectory = new TemporaryDirectory();
        string path = CreateJsonFile(tempDirectory.Path, "foundry.deploy.config.json", "{\"unattend\":" + manifest + "}");
        var service = new DeployConfigurationService(NullLogger<DeployConfigurationService>.Instance, path);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.Null(result.Document);
        Assert.NotEmpty(result.FailureMessage!);
    }

    [Fact]
    public void LoadOptional_WhenRemoteDiagnosticsConsentIsMissing_DefaultsToEnabled()
    {
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "foundry.deploy.config.json",
            """{ "schemaVersion": 11, "telemetry": { "isEnabled": true } }""");
        var service = new DeployConfigurationService(
            NullLogger<DeployConfigurationService>.Instance,
            configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.NotNull(result.Document);
        Assert.True(result.Document.Telemetry.IsRemoteDiagnosticsEnabled);
    }

    [Fact]
    public void LoadOptional_WhenSchemaIsOlderThanCurrent_RecommendsBootMediaUpdate()
    {
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "foundry.deploy.config.json",
            $$"""
            {
              "schemaVersion": {{Foundry.Core.Models.Configuration.ConfigurationSchemaVersions.DeployCurrent - 1}}
            }
            """);

        var logger = new RecordingLogger<DeployConfigurationService>();
        var service = new DeployConfigurationService(logger, configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.True(result.IsBootMediaUpdateRecommended);
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.LogLevel == LogLevel.Warning &&
                entry.Message.Contains("current schema version", StringComparison.Ordinal) &&
                entry.Message.Contains(Foundry.Core.Models.Configuration.ConfigurationSchemaVersions.DeployCurrent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void LoadOptional_WhenSchemaIsCurrent_DoesNotRecommendBootMediaUpdate()
    {
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "foundry.deploy.config.json",
            $$"""
            {
              "schemaVersion": {{FoundryDeployConfigurationDocument.CurrentSchemaVersion}}
            }
            """);

        var service = new DeployConfigurationService(
            NullLogger<DeployConfigurationService>.Instance,
            configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.True(result.Exists);
        Assert.NotNull(result.Document);
        Assert.Equal(FoundryDeployConfigurationDocument.CurrentSchemaVersion, result.Document.SchemaVersion);
        Assert.False(result.IsBootMediaUpdateRecommended);
    }

    [Fact]
    public void LoadOptional_WhenSchemaIsNewerThanSupported_ReturnsBlockingFailureBeforeValidation()
    {
        using var tempDirectory = new TemporaryDirectory();
        const string futureJson = """
            {
              "schemaVersion": 13,
              "unattend": null,
              "autopilot": {
                "provisioningMode": {
                  "futureMode": true
                }
              },
              "futurePolicy": {
                "requiresUnknownDeploymentStep": true
              }
            }
            """;
        string configurationPath = CreateJsonFile(tempDirectory.Path, "foundry.deploy.config.json", futureJson);
        byte[] originalBytes = File.ReadAllBytes(configurationPath);
        var service = new DeployConfigurationService(
            NullLogger<DeployConfigurationService>.Instance,
            configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.Null(result.Document);
        Assert.Contains("Foundry.Deploy", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("13", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("12", result.FailureMessage, StringComparison.Ordinal);
        Assert.True(result.IsUnsupportedSchemaVersion);
        Assert.Equal(originalBytes, File.ReadAllBytes(configurationPath));
    }

    [Fact]
    public void LoadOptional_WhenCompletionSettingsAreMissing_UsesAutomaticTenSecondReboot()
    {
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "foundry.deploy.config.json",
            $$"""
            {
              "schemaVersion": {{FoundryDeployConfigurationDocument.CurrentSchemaVersion}}
            }
            """);

        var service = new DeployConfigurationService(
            NullLogger<DeployConfigurationService>.Instance,
            configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.NotNull(result.Document);
        Assert.True(result.Document.Completion.AutomaticRebootEnabled);
        Assert.Equal(10, result.Document.Completion.AutomaticRebootDelaySeconds);
    }

    [Fact]
    public void LoadOptional_WhenLegacyMachineNamingUsesGeneratedSuffix_MigratesToComposition()
    {
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "foundry.deploy.config.json",
            """
            {
              "schemaVersion": 11,
              "customization": {
                "machineNaming": {
                  "isEnabled": true,
                  "prefix": "LAB-",
                  "autoGenerateName": true,
                  "allowManualSuffixEdit": false
                }
              }
            }
            """);

        var service = new DeployConfigurationService(
            NullLogger<DeployConfigurationService>.Instance,
            configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        DeployMachineNamingSettings naming = Assert.IsType<FoundryDeployConfigurationDocument>(result.Document)
            .Customization.MachineNaming;
        Assert.Equal(Foundry.Core.Models.Configuration.MachineNamingMode.Composed, naming.Mode);
        Assert.False(naming.AllowEditingDuringDeployment);
        Assert.Collection(
            naming.Components,
            component =>
            {
                Assert.Equal(Foundry.Core.Models.Configuration.MachineNameComponentType.StaticText, component.Type);
                Assert.Equal("LAB-", component.StaticText);
            },
            component =>
            {
                Assert.Equal(Foundry.Core.Models.Configuration.MachineNameComponentType.Random, component.Type);
                Assert.Equal(6, component.MaximumLength);
            });
    }

    [Fact]
    public void LoadOptional_WhenLegacyConfigurationContainsNetworkProfileRoaming_MigratesBothTransports()
    {
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "foundry.deploy.config.json",
            $$"""
            {
              "schemaVersion": 11,
              "network": {
                "profileRoaming": {
                  "isEnabled": true,
                  "includePrivateKeyMaterial": true
                }
              }
            }
            """);

        var service = new DeployConfigurationService(
            NullLogger<DeployConfigurationService>.Instance,
            configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.NotNull(result.Document);
        Assert.True(result.Document.Network.ProfileRoaming.WiredDot1x.IsEnabled);
        Assert.True(result.Document.Network.ProfileRoaming.WiredDot1x.IncludePrivateKeyMaterial);
        Assert.True(result.Document.Network.ProfileRoaming.Wifi.IsEnabled);
        Assert.True(result.Document.Network.ProfileRoaming.Wifi.IncludePrivateKeyMaterial);
    }

    [Fact]
    public void LoadOptional_WhenSplitRoamingSettingsArePartial_BackfillsOnlyMissingFieldsFromLegacySettings()
    {
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "foundry.deploy.config.json",
            """
            {
              "schemaVersion": 12,
              "network": {
                "profileRoaming": {
                  "isEnabled": true,
                  "includePrivateKeyMaterial": true,
                  "wiredDot1x": {
                    "isEnabled": true
                  },
                  "wifi": {
                    "includePrivateKeyMaterial": false
                  }
                }
              }
            }
            """);

        var service = new DeployConfigurationService(
            NullLogger<DeployConfigurationService>.Instance,
            configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.NotNull(result.Document);
        Assert.True(result.Document.Network.ProfileRoaming.WiredDot1x.IsEnabled);
        Assert.True(result.Document.Network.ProfileRoaming.WiredDot1x.IncludePrivateKeyMaterial);
        Assert.True(result.Document.Network.ProfileRoaming.Wifi.IsEnabled);
        Assert.False(result.Document.Network.ProfileRoaming.Wifi.IncludePrivateKeyMaterial);
    }

    [Fact]
    public void LoadOptional_WhenSplitRoamingSettingsAreExplicit_DoesNotLetLegacyFallbackOverrideThem()
    {
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "foundry.deploy.config.json",
            """
            {
              "schemaVersion": 12,
              "network": {
                "profileRoaming": {
                  "wiredDot1x": {
                    "isEnabled": false,
                    "includePrivateKeyMaterial": false
                  },
                  "wifi": {
                    "isEnabled": false,
                    "includePrivateKeyMaterial": false
                  },
                  "isEnabled": true,
                  "includePrivateKeyMaterial": true
                }
              }
            }
            """);

        var service = new DeployConfigurationService(
            NullLogger<DeployConfigurationService>.Instance,
            configurationPath);

        DeployConfigurationLoadResult result = service.LoadOptional();

        Assert.NotNull(result.Document);
        Assert.False(result.Document.Network.ProfileRoaming.WiredDot1x.IsEnabled);
        Assert.False(result.Document.Network.ProfileRoaming.WiredDot1x.IncludePrivateKeyMaterial);
        Assert.False(result.Document.Network.ProfileRoaming.Wifi.IsEnabled);
        Assert.False(result.Document.Network.ProfileRoaming.Wifi.IncludePrivateKeyMaterial);
    }

    private static string CreateJsonFile(string directoryPath, string fileName, string contents)
    {
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Foundry.Deploy.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, string Message);
}
