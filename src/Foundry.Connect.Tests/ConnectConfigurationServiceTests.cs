// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Foundry.Connect.Models.Configuration;
using Foundry.Connect.Services.Configuration;
using Foundry.Core.Services.Configuration;
using Foundry.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreConfiguration = Foundry.Core.Models.Configuration;

namespace Foundry.Connect.Tests;

public sealed class ConnectConfigurationServiceTests
{
    [Fact]
    public void Load_WhenNoConfigurationFileIsAvailable_ReturnsNormalizedDefaults()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        var service = new ConnectConfigurationService([], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.False(service.IsLoadedFromDisk);
        Assert.Null(service.ConfigurationPath);
        Assert.Equal(FoundryConnectConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.Equal(5, configuration.InternetProbe.TimeoutSeconds);
        Assert.Equal(
            ["http://www.msftconnecttest.com/connecttest.txt", "http://www.google.com"],
            configuration.InternetProbe.ProbeUris);
    }

    [Fact]
    public void Load_WhenConfigurationFileContainsMixedProbeUris_NormalizesValues()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "normalized.json",
            """
            {
              "schemaVersion": 0,
              "internetProbe": {
                "probeUris": [
                  " https://example.com/health ",
                  "invalid-uri",
                  "https://example.com/health",
                  "http://contoso.test/connect"
                ],
                "timeoutSeconds": 99
              }
            }
            """);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.True(service.IsLoadedFromDisk);
        Assert.Equal(System.IO.Path.GetFullPath(configurationPath), service.ConfigurationPath);
        Assert.Equal(FoundryConnectConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.Equal(30, configuration.InternetProbe.TimeoutSeconds);
        Assert.Equal(
            ["https://example.com/health", "http://contoso.test/connect"],
            configuration.InternetProbe.ProbeUris);
    }

    [Fact]
    public void Load_WhenSchemaIsOlderThanCurrent_RecommendsBootMediaUpdate()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "older-than-current.json",
            $$"""
            {
              "schemaVersion": {{CoreConfiguration.ConfigurationSchemaVersions.ConnectCurrent - 1}}
            }
            """);

        var logger = new RecordingLogger<ConnectConfigurationService>();
        var service = new ConnectConfigurationService(["--config", configurationPath], logger);

        service.Load();

        Assert.True(service.IsBootMediaUpdateRecommended);
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.LogLevel == LogLevel.Warning &&
                entry.Message.Contains("current schema version", StringComparison.Ordinal) &&
                entry.Message.Contains(CoreConfiguration.ConfigurationSchemaVersions.ConnectCurrent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Load_WhenSchemaMatchesCurrent_DoesNotRecommendBootMediaUpdate()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "current.json",
            $$"""
            {
              "schemaVersion": {{CoreConfiguration.ConfigurationSchemaVersions.ConnectCurrent}}
            }
            """);

        var logger = new RecordingLogger<ConnectConfigurationService>();
        var service = new ConnectConfigurationService(["--config", configurationPath], logger);

        service.Load();

        Assert.False(service.IsBootMediaUpdateRecommended);
        Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void Load_WhenSchemaIsNewerThanSupported_FailsBeforeReadingSecrets()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        const string futureJson = """
            {
              "schemaVersion": 5,
              "wifi": {
                "enterpriseAuthenticationMode": {
                  "futureMode": true
                },
                "passphraseSecret": {
                  "kind": "future-secret-format"
                }
              }
            }
            """;
        string configurationPath = CreateJsonFile(tempDirectory.Path, "future.json", futureJson);
        byte[] originalBytes = File.ReadAllBytes(configurationPath);
        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfigurationException exception = Assert.Throws<FoundryConnectConfigurationException>(service.Load);

        UnsupportedConfigurationVersionException unsupported = Assert.IsType<UnsupportedConfigurationVersionException>(exception.InnerException);
        Assert.Contains("Foundry.Connect", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Foundry.Connect", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("5", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("4", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains(configurationPath, exception.Message, StringComparison.Ordinal);
        Assert.False(service.IsLoadedFromDisk);
        Assert.Equal(originalBytes, File.ReadAllBytes(configurationPath));
    }

    [Fact]
    public void Load_WhenConfigurationContainsTelemetry_PreservesTelemetrySettings()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "telemetry.json",
            $$"""
            {
              "schemaVersion": {{FoundryConnectConfiguration.CurrentSchemaVersion}},
              "telemetry": {
                "isEnabled": false,
                "isRemoteDiagnosticsEnabled": true,
                "installId": "install-id",
                "hostUrl": "https://eu.i.posthog.com",
                "projectToken": "project-token",
                "runtimePayloadSource": "debug"
              }
            }
            """);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.False(configuration.Telemetry.IsEnabled);
        Assert.True(configuration.Telemetry.IsRemoteDiagnosticsEnabled);
        Assert.False(service.IsBootMediaUpdateRecommended);
        Assert.Equal("install-id", configuration.Telemetry.InstallId);
        Assert.Equal(TelemetryDefaults.PostHogEuHost, configuration.Telemetry.HostUrl);
        Assert.Equal("project-token", configuration.Telemetry.ProjectToken);
        Assert.Equal(TelemetryRuntimePayloadSources.Debug, configuration.Telemetry.RuntimePayloadSource);
    }

    [Fact]
    public void Load_WhenRemoteDiagnosticsConsentIsMissing_DefaultsToEnabled()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "legacy-telemetry.json",
            """{ "schemaVersion": 3, "telemetry": { "isEnabled": true } }""");
        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.True(configuration.Telemetry.IsRemoteDiagnosticsEnabled);
    }

    [Fact]
    public void Load_WhenEnvironmentVariableIsSet_TakesPrecedenceOverCommandLineArgument()
    {
        using var tempDirectory = new TemporaryDirectory();
        string environmentConfigurationPath = CreateJsonFile(tempDirectory.Path, "environment.json", """{ "schemaVersion": 4 }""");
        string commandLineConfigurationPath = CreateJsonFile(tempDirectory.Path, "argument.json", """{ "schemaVersion": 2 }""");
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", environmentConfigurationPath);

        var service = new ConnectConfigurationService(["--config", commandLineConfigurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.True(service.IsLoadedFromDisk);
        Assert.Equal(System.IO.Path.GetFullPath(environmentConfigurationPath), service.ConfigurationPath);
        Assert.Equal(4, configuration.SchemaVersion);
    }

    [Fact]
    public void Load_WhenCoreGeneratedConfigurationIsProvided_PreservesEffectiveNetworkPaths()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string wiredProfilePath = CreateFile(tempDirectory.Path, "wired.xml", "<LANProfile />");
        string configurationJson = new ConnectConfigurationGenerator().CreateProvisioningBundle(
            new CoreConfiguration.FoundryConfigurationDocument
            {
                Network = new CoreConfiguration.NetworkSettings
                {
                    Dot1x = new CoreConfiguration.Dot1xSettings
                    {
                        IsEnabled = true,
                        ProfileTemplatePath = wiredProfilePath
                    }
                }
            },
            tempDirectory.Path).ConfigurationJson;
        string configurationPath = CreateJsonFile(tempDirectory.Path, "core-generated.json", configurationJson);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.True(service.IsLoadedFromDisk);
        Assert.Equal(CoreConfiguration.FoundryConnectConfigurationDocument.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.Equal(@"Network\Wired\Profiles\wired.xml", configuration.Dot1x.ProfileTemplatePath);
        Assert.NotNull(configuration.Capabilities);
        Assert.NotNull(configuration.Wifi);
        Assert.NotNull(configuration.InternetProbe);
    }

    [Fact]
    public void Load_WhenCoreGeneratedConfigurationContainsNetworkProfileRoaming_PreservesOptIn()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationJson = new ConnectConfigurationGenerator().CreateProvisioningBundle(
            new CoreConfiguration.FoundryConfigurationDocument
            {
                Network = new CoreConfiguration.NetworkSettings
                {
                    RoamWiredDot1xProfileToWindows = true,
                    RoamWiredDot1xPrivateKeyMaterialToWindows = true,
                    RoamWifiProfileToWindows = false
                }
            },
            tempDirectory.Path).ConfigurationJson;
        string configurationPath = CreateJsonFile(tempDirectory.Path, "core-generated.json", configurationJson);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.True(configuration.Network.ProfileRoaming.WiredDot1x.IsEnabled);
        Assert.True(configuration.Network.ProfileRoaming.WiredDot1x.IncludePrivateKeyMaterial);
        Assert.False(configuration.Network.ProfileRoaming.Wifi.IsEnabled);
    }

    [Fact]
    public void Load_WhenOnlyLegacyProfileRoamingSettingsAreProvided_MigratesBothTransports()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "legacy-roaming.json",
            """
            {
              "network": {
                "profileRoaming": {
                  "isEnabled": true,
                  "includePrivateKeyMaterial": true
                }
              }
            }
            """);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.True(configuration.Network.ProfileRoaming.WiredDot1x.IsEnabled);
        Assert.True(configuration.Network.ProfileRoaming.WiredDot1x.IncludePrivateKeyMaterial);
        Assert.True(configuration.Network.ProfileRoaming.Wifi.IsEnabled);
        Assert.True(configuration.Network.ProfileRoaming.Wifi.IncludePrivateKeyMaterial);
    }

    [Fact]
    public void Load_WhenSplitRoamingSettingsArePartial_BackfillsOnlyMissingFieldsFromLegacySettings()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "partial-roaming.json",
            """
            {
              "network": {
                "profileRoaming": {
                  "wiredDot1x": {
                    "isEnabled": true
                  },
                  "wifi": {
                    "includePrivateKeyMaterial": false
                  },
                  "isEnabled": true,
                  "includePrivateKeyMaterial": true
                }
              }
            }
            """);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.True(configuration.Network.ProfileRoaming.WiredDot1x.IsEnabled);
        Assert.True(configuration.Network.ProfileRoaming.WiredDot1x.IncludePrivateKeyMaterial);
        Assert.True(configuration.Network.ProfileRoaming.Wifi.IsEnabled);
        Assert.False(configuration.Network.ProfileRoaming.Wifi.IncludePrivateKeyMaterial);
    }

    [Fact]
    public void Load_WhenSplitRoamingSettingsAreExplicit_DoesNotLetLegacyFallbackOverrideThem()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "mixed-roaming.json",
            """
            {
              "schemaVersion": 3,
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

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.False(configuration.Network.ProfileRoaming.WiredDot1x.IsEnabled);
        Assert.False(configuration.Network.ProfileRoaming.WiredDot1x.IncludePrivateKeyMaterial);
        Assert.False(configuration.Network.ProfileRoaming.Wifi.IsEnabled);
        Assert.False(configuration.Network.ProfileRoaming.Wifi.IncludePrivateKeyMaterial);
    }

    [Fact]
    public void Load_WhenCoreGeneratedConfigurationContainsEncryptedPassphrase_DecryptsPassphraseFromMediaKey()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        Foundry.Core.Models.Configuration.FoundryConnectProvisioningBundle bundle =
            new ConnectConfigurationGenerator().CreateProvisioningBundle(
                new CoreConfiguration.FoundryConfigurationDocument
                {
                    Network = new CoreConfiguration.NetworkSettings
                    {
                        WifiProvisioned = true,
                        Wifi = new CoreConfiguration.WifiSettings
                        {
                            IsEnabled = true,
                            Ssid = "Corp WiFi",
                            SecurityType = "WPA2/WPA3-Personal",
                            Passphrase = "super-secret-passphrase"
                        }
                    }
                },
                tempDirectory.Path);
        Assert.NotNull(bundle.MediaSecretsKey);
        Assert.DoesNotContain("super-secret-passphrase", bundle.ConfigurationJson, StringComparison.Ordinal);

        string configDirectory = System.IO.Path.Combine(tempDirectory.Path, "Config");
        Directory.CreateDirectory(configDirectory);
        string configurationPath = CreateJsonFile(configDirectory, "foundry.connect.config.json", bundle.ConfigurationJson);
        CreateBinaryFile(System.IO.Path.Combine(configDirectory, "Secrets"), "media-secrets.key", bundle.MediaSecretsKey);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.Equal("super-secret-passphrase", configuration.Wifi.Passphrase);
        Assert.Null(configuration.Wifi.PassphraseSecret);
    }

    [Fact]
    public void Load_WhenCoreGeneratedConfigurationContainsEncryptedPfxPasswords_DecryptsPasswordsFromMediaKey()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        Foundry.Core.Models.Configuration.FoundryConnectProvisioningBundle bundle =
            new ConnectConfigurationGenerator().CreateProvisioningBundle(
                new CoreConfiguration.FoundryConfigurationDocument
                {
                    Network = new CoreConfiguration.NetworkSettings
                    {
                        WifiProvisioned = true,
                        Dot1x = new CoreConfiguration.Dot1xSettings
                        {
                            IsEnabled = true,
                            ProfileTemplatePath = CreateFile(tempDirectory.Path, "wired.xml", "<LANProfile />"),
                            RequiresCertificate = true,
                            CertificatePath = CreateFile(tempDirectory.Path, "wired.pfx", "wired-pfx"),
                            CertificatePfxPassword = "wired-password"
                        },
                        Wifi = new CoreConfiguration.WifiSettings
                        {
                            IsEnabled = true,
                            Ssid = "Corp WiFi",
                            SecurityType = NetworkConfigurationValidator.WifiSecurityEnterprise,
                            HasEnterpriseProfile = true,
                            EnterpriseProfileTemplatePath = CreateFile(
                                tempDirectory.Path,
                                "wifi.xml",
                                """
                                <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                                  <MSM>
                                    <security>
                                      <authEncryption>
                                        <authentication>WPA2</authentication>
                                      </authEncryption>
                                    </security>
                                  </MSM>
                                </WLANProfile>
                                """),
                            RequiresCertificate = true,
                            CertificatePath = CreateFile(tempDirectory.Path, "wifi.pfx", "wifi-pfx"),
                            CertificatePfxPassword = "wifi-password"
                        }
                    }
                },
                tempDirectory.Path);
        Assert.NotNull(bundle.MediaSecretsKey);
        Assert.DoesNotContain("wired-password", bundle.ConfigurationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("wifi-password", bundle.ConfigurationJson, StringComparison.Ordinal);

        string configDirectory = System.IO.Path.Combine(tempDirectory.Path, "Config");
        Directory.CreateDirectory(configDirectory);
        string configurationPath = CreateJsonFile(configDirectory, "foundry.connect.config.json", bundle.ConfigurationJson);
        CreateBinaryFile(System.IO.Path.Combine(configDirectory, "Secrets"), "media-secrets.key", bundle.MediaSecretsKey);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.Equal("wired-password", configuration.Dot1x.CertificatePfxPassword);
        Assert.Equal("wifi-password", configuration.Wifi.CertificatePfxPassword);
        Assert.NotNull(configuration.Dot1x.CertificatePfxPasswordSecret);
        Assert.NotNull(configuration.Wifi.CertificatePfxPasswordSecret);
    }

    [Fact]
    public void Load_WhenEncryptedPassphraseHasNoMediaKey_ThrowsConfigurationException()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        Foundry.Core.Models.Configuration.FoundryConnectProvisioningBundle bundle =
            new ConnectConfigurationGenerator().CreateProvisioningBundle(
                new CoreConfiguration.FoundryConfigurationDocument
                {
                    Network = new CoreConfiguration.NetworkSettings
                    {
                        WifiProvisioned = true,
                        Wifi = new CoreConfiguration.WifiSettings
                        {
                            IsEnabled = true,
                            Ssid = "Corp WiFi",
                            SecurityType = "WPA2/WPA3-Personal",
                            Passphrase = "super-secret-passphrase"
                        }
                    }
                },
                tempDirectory.Path);
        string configDirectory = System.IO.Path.Combine(tempDirectory.Path, "Config");
        Directory.CreateDirectory(configDirectory);
        string configurationPath = CreateJsonFile(configDirectory, "foundry.connect.config.json", bundle.ConfigurationJson);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfigurationException exception = Assert.Throws<FoundryConnectConfigurationException>(service.Load);
        Assert.Contains("Media secret key file was not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WhenEncryptedPassphraseUsesUnsupportedEnvelope_ThrowsConfigurationException()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        Foundry.Core.Models.Configuration.FoundryConnectProvisioningBundle bundle = CreatePersonalWifiProvisioningBundle(tempDirectory.Path);
        Assert.NotNull(bundle.MediaSecretsKey);

        string configDirectory = System.IO.Path.Combine(tempDirectory.Path, "Config");
        Directory.CreateDirectory(configDirectory);
        string configurationJson = ReplacePassphraseSecretProperty(bundle.ConfigurationJson, "algorithm", "unsupported");
        string configurationPath = CreateJsonFile(configDirectory, "foundry.connect.config.json", configurationJson);
        CreateBinaryFile(System.IO.Path.Combine(configDirectory, "Secrets"), "media-secrets.key", bundle.MediaSecretsKey);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfigurationException exception = Assert.Throws<FoundryConnectConfigurationException>(service.Load);
        Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-passphrase", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WhenEncryptedPassphraseIsTampered_ThrowsConfigurationException()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        Foundry.Core.Models.Configuration.FoundryConnectProvisioningBundle bundle = CreatePersonalWifiProvisioningBundle(tempDirectory.Path);
        Assert.NotNull(bundle.MediaSecretsKey);

        string configDirectory = System.IO.Path.Combine(tempDirectory.Path, "Config");
        Directory.CreateDirectory(configDirectory);
        string configurationJson = ReplacePassphraseSecretProperty(bundle.ConfigurationJson, "ciphertext", "AA");
        string configurationPath = CreateJsonFile(configDirectory, "foundry.connect.config.json", configurationJson);
        CreateBinaryFile(System.IO.Path.Combine(configDirectory, "Secrets"), "media-secrets.key", bundle.MediaSecretsKey);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfigurationException exception = Assert.Throws<FoundryConnectConfigurationException>(service.Load);
        Assert.Contains("could not be decrypted", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-passphrase", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("AA", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WhenLegacyPlaintextPassphraseIsProvided_PreservesPassphrase()
    {
        using var environmentScope = new EnvironmentVariableScope("FOUNDRY_CONNECT_CONFIG", null);
        using var tempDirectory = new TemporaryDirectory();
        string configurationPath = CreateJsonFile(
            tempDirectory.Path,
            "legacy.json",
            """
            {
              "schemaVersion": 1,
              "capabilities": {
                "wifiProvisioned": true
              },
              "wifi": {
                "isEnabled": true,
                "ssid": "Corp WiFi",
                "securityType": "WPA2/WPA3-Personal",
                "passphrase": "legacy-passphrase"
              }
            }
            """);

        var service = new ConnectConfigurationService(["--config", configurationPath], NullLogger<ConnectConfigurationService>.Instance);

        FoundryConnectConfiguration configuration = service.Load();

        Assert.Equal("legacy-passphrase", configuration.Wifi.Passphrase);
        Assert.Null(configuration.Wifi.PassphraseSecret);
    }

    private static string CreateJsonFile(string directoryPath, string fileName, string contents)
    {
        string filePath = System.IO.Path.Combine(directoryPath, fileName);
        using JsonDocument document = JsonDocument.Parse(contents);
        File.WriteAllText(filePath, document.RootElement.GetRawText());
        return filePath;
    }

    private static Foundry.Core.Models.Configuration.FoundryConnectProvisioningBundle CreatePersonalWifiProvisioningBundle(string stagingDirectoryPath)
    {
        return new ConnectConfigurationGenerator().CreateProvisioningBundle(
            new CoreConfiguration.FoundryConfigurationDocument
            {
                Network = new CoreConfiguration.NetworkSettings
                {
                    WifiProvisioned = true,
                    Wifi = new CoreConfiguration.WifiSettings
                    {
                        IsEnabled = true,
                        Ssid = "Corp WiFi",
                        SecurityType = "WPA2/WPA3-Personal",
                        Passphrase = "super-secret-passphrase"
                    }
                }
            },
            stagingDirectoryPath);
    }

    private static string ReplacePassphraseSecretProperty(string configurationJson, string propertyName, string value)
    {
        JsonNode root = JsonNode.Parse(configurationJson)!;
        root["wifi"]!["passphraseSecret"]![propertyName] = value;
        return root.ToJsonString();
    }

    private static string CreateBinaryFile(string directoryPath, string fileName, byte[] contents)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = System.IO.Path.Combine(directoryPath, fileName);
        File.WriteAllBytes(filePath, contents);
        return filePath;
    }

    private static string CreateFile(string directoryPath, string fileName, string contents)
    {
        string filePath = System.IO.Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Foundry.Connect.Tests", Guid.NewGuid().ToString("N"));
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
