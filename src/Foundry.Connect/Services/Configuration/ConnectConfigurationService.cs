// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Foundry.Connect.Models.Configuration;
using Foundry.Core.Services.Configuration;
using ConfigurationSchemaVersions = Foundry.Core.Models.Configuration.ConfigurationSchemaVersions;
using CoreConnectNetworkSettings = Foundry.Core.Models.Configuration.ConnectNetworkSettings;
using Foundry.Connect.Services.Runtime;
using Microsoft.Extensions.Logging;

namespace Foundry.Connect.Services.Configuration;

/// <summary>
/// Resolves, reads, decrypts, and normalizes Foundry.Connect runtime configuration.
/// </summary>
public sealed class ConnectConfigurationService : IConnectConfigurationService
{
    private const string DefaultConfigFileName = "foundry.connect.config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string[] _args;
    private readonly ILogger<ConnectConfigurationService> _logger;

    /// <summary>
    /// Initializes a configuration service.
    /// </summary>
    /// <param name="args">Command-line arguments used to resolve configuration.</param>
    /// <param name="logger">The logger used for configuration diagnostics.</param>
    public ConnectConfigurationService(string[] args, ILogger<ConnectConfigurationService> logger)
    {
        _args = args ?? Array.Empty<string>();
        _logger = logger;
    }

    /// <inheritdoc />
    public string? ConfigurationPath { get; private set; }

    /// <inheritdoc />
    public bool IsLoadedFromDisk { get; private set; }

    /// <inheritdoc />
    public bool IsBootMediaUpdateRecommended { get; private set; }

    /// <inheritdoc />
    public FoundryConnectConfiguration Load()
    {
        ConfigurationResolution resolution = ResolveConfigurationPath(_args);
        ConfigurationPath = resolution.Path;
        if (string.IsNullOrWhiteSpace(ConfigurationPath))
        {
            IsLoadedFromDisk = false;
            ResetSchemaCompatibilityState();
            _logger.LogInformation("No Foundry.Connect configuration file was resolved. Using built-in defaults.");
            return Normalize(new FoundryConnectConfiguration());
        }

        string fullPath = Path.GetFullPath(ConfigurationPath);
        if (!File.Exists(fullPath))
        {
            if (!resolution.IsRequired)
            {
                ConfigurationPath = null;
                IsLoadedFromDisk = false;
                ResetSchemaCompatibilityState();
                _logger.LogInformation("Foundry.Connect configuration file was not found. Using built-in defaults. ConfigurationPath={ConfigurationPath}", fullPath);
                return Normalize(new FoundryConnectConfiguration());
            }

            throw new FoundryConnectConfigurationException($"Configuration file was not found: {fullPath}");
        }

        try
        {
            _logger.LogInformation("Loading Foundry.Connect configuration from disk. ConfigurationPath={ConfigurationPath}", fullPath);
            string json = File.ReadAllText(fullPath);
            ConfigurationVersionGuard.ThrowIfUnsupported(
                json,
                "Foundry.Connect",
                ConfigurationSchemaVersions.ConnectCurrent,
                JsonOptions);
            FoundryConnectConfiguration? configuration = JsonSerializer.Deserialize<FoundryConnectConfiguration>(json, JsonOptions);
            if (configuration is null)
            {
                throw new FoundryConnectConfigurationException($"Configuration file is empty or invalid: {fullPath}");
            }

            if (configuration.SchemaVersion > ConfigurationSchemaVersions.ConnectCurrent)
            {
                throw new UnsupportedConfigurationVersionException(
                    "Foundry.Connect",
                    configuration.SchemaVersion,
                    ConfigurationSchemaVersions.ConnectCurrent);
            }

            ApplySchemaCompatibilityState(configuration.SchemaVersion);
            configuration = DecryptEmbeddedSecrets(configuration, fullPath);
            ConfigurationPath = fullPath;
            IsLoadedFromDisk = true;
            _logger.LogInformation("Loaded Foundry.Connect configuration from disk. ConfigurationPath={ConfigurationPath}", fullPath);
            return Normalize(configuration);
        }
        catch (FoundryConnectConfigurationException)
        {
            throw;
        }
        catch (UnsupportedConfigurationVersionException ex)
        {
            throw new FoundryConnectConfigurationException(
                $"{ex.Message}{Environment.NewLine}Configuration path: {fullPath}",
                ex);
        }
        catch (Exception ex)
        {
            throw new FoundryConnectConfigurationException($"Configuration file could not be parsed: {fullPath}", ex);
        }
    }

    private void ResetSchemaCompatibilityState()
    {
        IsBootMediaUpdateRecommended = false;
    }

    private void ApplySchemaCompatibilityState(int schemaVersion)
    {
        IsBootMediaUpdateRecommended = ConfigurationSchemaVersions.IsBootMediaUpdateRecommended(
            schemaVersion,
            ConfigurationSchemaVersions.ConnectCurrent);
        if (IsBootMediaUpdateRecommended)
        {
            _logger.LogWarning(
                "Foundry.Connect configuration uses schema version {SchemaVersion}, older than current schema version {CurrentSchemaVersion}. Boot media update is recommended.",
                schemaVersion,
                ConfigurationSchemaVersions.ConnectCurrent);
        }
    }

    private static ConfigurationResolution ResolveConfigurationPath(IEnumerable<string> args)
    {
        string? envPath = Environment.GetEnvironmentVariable("FOUNDRY_CONNECT_CONFIG");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return new ConfigurationResolution(envPath, IsRequired: true);
        }

        string[] values = args.ToArray();
        for (int index = 0; index < values.Length; index++)
        {
            string value = values[index];
            if (value.Equals("--config", StringComparison.OrdinalIgnoreCase) && index + 1 < values.Length)
            {
                return new ConfigurationResolution(values[index + 1], IsRequired: true);
            }
        }

        return new ConfigurationResolution(
            ConnectWorkspacePaths.GetConfigFilePath(DefaultConfigFileName),
            IsRequired: ConnectWorkspacePaths.IsWinPeRuntime());
    }

    private static FoundryConnectConfiguration Normalize(FoundryConnectConfiguration configuration)
    {
        NetworkCapabilitiesOptions capabilities = configuration.Capabilities ?? new NetworkCapabilitiesOptions();
        InternetProbeOptions probe = configuration.InternetProbe ?? new InternetProbeOptions();

        string[] probeUris = (probe.ProbeUris ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Where(static value => Uri.TryCreate(value, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (probeUris.Length == 0)
        {
            // Keep internet detection useful even when the generated configuration omits explicit probe endpoints.
            probeUris =
            [
                "http://www.msftconnecttest.com/connecttest.txt",
                "http://www.google.com"
            ];
        }

        return new FoundryConnectConfiguration
        {
            SchemaVersion = configuration.SchemaVersion <= 0
                ? FoundryConnectConfiguration.CurrentSchemaVersion
                : configuration.SchemaVersion,
            Capabilities = new NetworkCapabilitiesOptions
            {
                WifiProvisioned = capabilities.WifiProvisioned
            },
            Network = configuration.Network ?? new CoreConnectNetworkSettings(),
            Dot1x = NormalizeDot1x(configuration.Dot1x),
            Wifi = NormalizeWifi(configuration.Wifi),
            InternetProbe = new InternetProbeOptions
            {
                ProbeUris = probeUris,
                TimeoutSeconds = Math.Clamp(probe.TimeoutSeconds, 1, 30)
            },
            Telemetry = configuration.Telemetry ?? new Foundry.Telemetry.TelemetrySettings()
        };
    }

    private static FoundryConnectConfiguration DecryptEmbeddedSecrets(
        FoundryConnectConfiguration configuration,
        string configurationPath)
    {
        WifiSettings? wifi = configuration.Wifi;
        Dot1xSettings? dot1x = configuration.Dot1x;
        if (wifi?.PassphraseSecret is null &&
            wifi?.CertificatePfxPasswordSecret is null &&
            dot1x?.CertificatePfxPasswordSecret is null)
        {
            return configuration;
        }

        byte[] mediaSecretsKey = LoadMediaSecretsKey(configurationPath);
        string? passphrase = null;
        string? wifiCertificatePfxPassword = null;
        string? wiredCertificatePfxPassword = null;
        try
        {
            if (wifi?.PassphraseSecret is not null)
            {
                passphrase = ConnectSecretEnvelopeProtector.Decrypt(wifi.PassphraseSecret, mediaSecretsKey);
            }

            if (wifi?.CertificatePfxPasswordSecret is not null)
            {
                wifiCertificatePfxPassword = ConnectSecretEnvelopeProtector.Decrypt(wifi.CertificatePfxPasswordSecret, mediaSecretsKey);
            }

            if (dot1x?.CertificatePfxPasswordSecret is not null)
            {
                wiredCertificatePfxPassword = ConnectSecretEnvelopeProtector.Decrypt(dot1x.CertificatePfxPasswordSecret, mediaSecretsKey);
            }
        }
        finally
        {
            // The media key should not remain in memory after decrypting the runtime Wi-Fi passphrase.
            CryptographicOperations.ZeroMemory(mediaSecretsKey);
        }

        return new FoundryConnectConfiguration
        {
            SchemaVersion = configuration.SchemaVersion,
            Capabilities = configuration.Capabilities,
            Network = configuration.Network,
            Dot1x = dot1x is null
                ? new Dot1xSettings()
                : dot1x with
                {
                    CertificatePfxPassword = wiredCertificatePfxPassword,
                    CertificatePfxPasswordSecret = dot1x.CertificatePfxPasswordSecret
                },
            Wifi = (wifi ?? new WifiSettings()) with
            {
                Passphrase = passphrase ?? wifi?.Passphrase,
                PassphraseSecret = null,
                CertificatePfxPassword = wifiCertificatePfxPassword,
                CertificatePfxPasswordSecret = wifi?.CertificatePfxPasswordSecret
            },
            InternetProbe = configuration.InternetProbe,
            Telemetry = configuration.Telemetry
        };
    }

    private static WifiSettings NormalizeWifi(WifiSettings? wifi)
    {
        return wifi is null
            ? new WifiSettings()
            : wifi with
            {
                EnterpriseAuthenticationMode = Enum.IsDefined(wifi.EnterpriseAuthenticationMode)
                    ? wifi.EnterpriseAuthenticationMode
                    : NetworkAuthenticationMode.UserOnly,
                PassphraseSecret = null
            };
    }

    private static Dot1xSettings NormalizeDot1x(Dot1xSettings? dot1x)
    {
        return dot1x is null
            ? new Dot1xSettings()
            : dot1x with
            {
                AuthenticationMode = Enum.IsDefined(dot1x.AuthenticationMode)
                    ? dot1x.AuthenticationMode
                    : NetworkAuthenticationMode.MachineOnly
            };
    }

    private static byte[] LoadMediaSecretsKey(string configurationPath)
    {
        string? configurationDirectory = Path.GetDirectoryName(configurationPath);
        if (string.IsNullOrWhiteSpace(configurationDirectory))
        {
            throw new FoundryConnectConfigurationException("Configuration directory could not be resolved for encrypted secrets.");
        }

        string keyPath = Path.Combine(configurationDirectory, "Secrets", "media-secrets.key");
        if (!File.Exists(keyPath))
        {
            throw new FoundryConnectConfigurationException("Media secret key file was not found for encrypted secrets.");
        }

        return File.ReadAllBytes(keyPath);
    }

    private readonly record struct ConfigurationResolution(string? Path, bool IsRequired);
}
