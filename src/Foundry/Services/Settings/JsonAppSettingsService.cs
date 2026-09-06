// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.WinPe;
using Foundry.Utilities.IO;
using Serilog;

namespace Foundry.Services.Settings;

/// <summary>
/// Persists application settings as JSON under the Foundry settings directory.
/// </summary>
/// <remarks>
/// Invalid settings files are moved aside with an <c>.invalid</c> suffix and replaced by defaults.
/// </remarks>
internal sealed partial class JsonAppSettingsService : IAppSettingsService
{
    private readonly ILogger logger;
    private readonly object saveLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonAppSettingsService"/> class.
    /// </summary>
    /// <param name="logger">Logger used for persistence and recovery diagnostics.</param>
    public JsonAppSettingsService(ILogger logger)
    {
        this.logger = logger.ForContext<JsonAppSettingsService>();
        IsFirstRun = !File.Exists(Constants.AppSettingsPath);
        Current = Load(out GeneralSettings? migratedGeneralSettings);
        MigratedGeneralSettings = migratedGeneralSettings;
        EnsureTelemetryInstallId(Current);
        Save();
    }

    /// <inheritdoc />
    public FoundryAppSettings Current { get; }

    /// <inheritdoc />
    public bool IsFirstRun { get; }

    /// <inheritdoc />
    public GeneralSettings? MigratedGeneralSettings { get; }

    /// <inheritdoc />
    public void Save()
    {
        lock (saveLock)
        {
            try
            {
                Directory.CreateDirectory(Constants.SettingsDirectoryPath);
                string json = JsonSerializer.Serialize(Current, FoundryAppSettingsJsonContext.Default.FoundryAppSettings);
                AtomicFile.WriteAllText(Constants.AppSettingsPath, json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to save app settings. SettingsPath={SettingsPath}", Constants.AppSettingsPath);
                throw;
            }
        }
    }

    private FoundryAppSettings Load(out GeneralSettings? migratedGeneralSettings)
    {
        migratedGeneralSettings = null;
        if (!File.Exists(Constants.AppSettingsPath))
        {
            return new FoundryAppSettings();
        }

        try
        {
            string json = File.ReadAllText(Constants.AppSettingsPath);
            migratedGeneralSettings = LegacyAppSettingsMediaMigration.TryReadGeneralSettings(json);
            FoundryAppSettings settings = NormalizeLoadedSettings(
                JsonSerializer.Deserialize(json, FoundryAppSettingsJsonContext.Default.FoundryAppSettings) ?? new FoundryAppSettings());
            ApplyLegacyAppearanceSettings(settings, json);
            return settings;
        }
        catch (Exception ex)
        {
            string backupPath = CreateInvalidBackupPath(Constants.AppSettingsPath);
            try
            {
                File.Move(Constants.AppSettingsPath, backupPath);
                logger.Warning(
                    ex,
                    "App settings file was invalid and defaults were restored. SettingsPath={SettingsPath}, BackupPath={BackupPath}",
                    Constants.AppSettingsPath,
                    backupPath);
            }
            catch (Exception backupException)
            {
                logger.Error(
                    backupException,
                    "Failed to back up invalid app settings. SettingsPath={SettingsPath}, BackupPath={BackupPath}, OriginalError={OriginalError}",
                    Constants.AppSettingsPath,
                    backupPath,
                    ex.Message);
                throw;
            }

            return new FoundryAppSettings();
        }
    }

    private static FoundryAppSettings NormalizeLoadedSettings(FoundryAppSettings settings)
    {
        settings.Appearance ??= new AppearanceSettings();
        settings.Localization ??= new LocalizationSettings();
        settings.Updates ??= new UpdateSettings();
        settings.Diagnostics ??= new DiagnosticsSettings();
        settings.Telemetry ??= new TelemetryAppSettings();
        settings.Proxy ??= new ProxyAppSettings();

        settings.Appearance.ElementTheme = string.IsNullOrWhiteSpace(settings.Appearance.ElementTheme)
            ? "Default"
            : settings.Appearance.ElementTheme;
        settings.Appearance.BackdropType = string.IsNullOrWhiteSpace(settings.Appearance.BackdropType)
            ? "Mica"
            : settings.Appearance.BackdropType;
        settings.Localization.Language = string.IsNullOrWhiteSpace(settings.Localization.Language)
            ? Foundry.Localization.FoundrySupportedCultures.DefaultCultureCode
            : settings.Localization.Language;
        settings.Updates.Channel = string.IsNullOrWhiteSpace(settings.Updates.Channel)
            ? Constants.DefaultUpdateChannel
            : settings.Updates.Channel;
        settings.Updates.FeedUrl = string.IsNullOrWhiteSpace(settings.Updates.FeedUrl)
            ? Constants.DefaultUpdateFeedUrl
            : settings.Updates.FeedUrl;
        settings.Proxy.Method = Enum.IsDefined(settings.Proxy.Method) ? settings.Proxy.Method : ProxyMethod.System;
        settings.Proxy.AuthenticationMode = Enum.IsDefined(settings.Proxy.AuthenticationMode)
            ? settings.Proxy.AuthenticationMode
            : ProxyAuthenticationMode.Automatic;
        settings.Proxy.Address ??= string.Empty;
        settings.Proxy.BypassList ??= string.Empty;
        settings.Proxy.Port = settings.Proxy.Port is >= 1 and <= 65535 ? settings.Proxy.Port : 8080;
        return settings;
    }

    private static string CreateInvalidBackupPath(string sourcePath)
    {
        string firstBackupPath = sourcePath + ".invalid";
        return !File.Exists(firstBackupPath)
            ? firstBackupPath
            : $"{firstBackupPath}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}";
    }

    private static void EnsureTelemetryInstallId(FoundryAppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Telemetry.InstallId))
        {
            settings.Telemetry.InstallId = Guid.NewGuid().ToString("D");
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(FoundryAppSettings))]
    private sealed partial class FoundryAppSettingsJsonContext : JsonSerializerContext;

    private static class LegacyAppSettingsMediaMigration
    {
        public static GeneralSettings? TryReadGeneralSettings(string json)
        {
            try
            {
                LegacyFoundryAppSettings? document = JsonSerializer.Deserialize(
                    json,
                    FoundryLegacyAppSettingsJsonContext.Default.LegacyFoundryAppSettings);

                return document?.Media?.ToGeneralSettings();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static void ApplyLegacyAppearanceSettings(FoundryAppSettings settings, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("appearance", out JsonElement appearance) ||
            appearance.ValueKind != JsonValueKind.Object ||
            appearance.TryGetProperty("elementTheme", out _))
        {
            return;
        }

        if (appearance.TryGetProperty("theme", out JsonElement legacyTheme) &&
            legacyTheme.ValueKind == JsonValueKind.String)
        {
            settings.Appearance.ElementTheme = legacyTheme.GetString()?.Trim().ToLowerInvariant() switch
            {
                "light" => "Light",
                "dark" => "Dark",
                _ => "Default"
            };
        }
    }

    private sealed class LegacyFoundryAppSettings
    {
        public LegacyMediaSettings? Media { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(LegacyFoundryAppSettings))]
    private sealed partial class FoundryLegacyAppSettingsJsonContext : JsonSerializerContext;
}
