// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Core.Models.Network;
using Foundry.Core.Services.Autopilot;

namespace Foundry.Core.Services.Configuration;

/// <summary>
/// Generates the reduced Foundry.Deploy runtime configuration from Foundry configuration settings.
/// </summary>
public sealed class DeployConfigurationGenerator : IDeployConfigurationGenerator
{
    /// <inheritdoc />
    public FoundryDeployConfigurationDocument Generate(FoundryConfigurationDocument document)
    {
        return Generate(document, deploymentSecretsKey: null, protectionSettings: null, oobeAccountSecretState: null);
    }

    /// <summary>
    /// Generates the reduced Foundry.Deploy runtime configuration and embeds encrypted media-only secrets when required.
    /// </summary>
    /// <param name="document">User-facing Foundry configuration.</param>
    /// <param name="deploymentSecretsKey">Deploy secret key used to encrypt boot-media-only secrets.</param>
    /// <returns>Reduced Foundry.Deploy configuration document.</returns>
    public FoundryDeployConfigurationDocument Generate(FoundryConfigurationDocument document, byte[]? deploymentSecretsKey)
    {
        return Generate(document, deploymentSecretsKey, protectionSettings: null, oobeAccountSecretState: null);
    }

    /// <summary>
    /// Generates the reduced Foundry.Deploy runtime configuration with Deploy secrets and protection metadata.
    /// </summary>
    /// <param name="document">User-facing Foundry configuration.</param>
    /// <param name="deploymentSecretsKey">Deploy secret key used to encrypt boot-media-only Deploy secrets.</param>
    /// <param name="protectionSettings">Deployment media password-protection metadata.</param>
    /// <returns>Reduced Foundry.Deploy configuration document.</returns>
    public FoundryDeployConfigurationDocument Generate(
        FoundryConfigurationDocument document,
        byte[]? deploymentSecretsKey,
        DeployProtectionSettings? protectionSettings)
    {
        return Generate(document, deploymentSecretsKey, protectionSettings, oobeAccountSecretState: null);
    }

    /// <inheritdoc />
    public FoundryDeployConfigurationDocument Generate(
        FoundryConfigurationDocument document,
        byte[]? deploymentSecretsKey,
        DeployProtectionSettings? protectionSettings,
        OobeAccountSecretState? oobeAccountSecretState)
    {
        ArgumentNullException.ThrowIfNull(document);
        UnattendFileService.ValidateSettings(document.Unattend, protectionSettings?.IsEnabled == true);
        AutopilotConfigurationValidator.ThrowIfNotReady(document.Autopilot, DateTimeOffset.UtcNow);
        MachineNamingValidator.ThrowIfInvalid(document.Customization.MachineNaming);
        OobeAccountConfigurationValidator.ThrowIfInvalid(document.Customization.Oobe, oobeAccountSecretState);
        ThrowIfAutopilotConflictsWithOobeAccounts(document.Autopilot, document.Customization.Oobe);
        ThrowIfOobePasswordsRequireProtectedMedia(document.Customization.Oobe, protectionSettings, deploymentSecretsKey);

        return new FoundryDeployConfigurationDocument
        {
            Protection = protectionSettings ?? new DeployProtectionSettings(),
            Unattend = document.Unattend.IsEnabled
                ? new DeployUnattendSettings
                {
                    IsEnabled = true,
                    DefaultFileId = document.Unattend.DefaultFileId,
                    Files = document.Unattend.Files.Select(file => new DeployUnattendFile
                    {
                        Id = file.Id,
                        DisplayName = file.DisplayName,
                        ContentHash = file.ContentHash
                    }).ToArray()
                }
                : new DeployUnattendSettings(),
            Completion = new DeployCompletionSettings
            {
                AutomaticRebootEnabled = document.General.AutomaticRebootEnabled,
                AutomaticRebootDelaySeconds = DeploymentRebootDelay.NormalizeRuntime(document.General.AutomaticRebootDelaySeconds)
            },
            OperatingSystemSelection = OperatingSystemSelectionSettingsNormalizer.ToDeploySettings(document.OperatingSystemSelection),
            Localization = new DeployLocalizationSettings
            {
                DefaultTimeZoneId = document.Localization.DefaultTimeZoneId
            },
            Network = new DeployNetworkSettings
            {
                ProfileRoaming = new DeployNetworkProfileRoamingSettings
                {
                    WiredDot1x = new NetworkProfileRoamingTransportSettings
                    {
                        IsEnabled = document.Network.RoamWiredDot1xProfileToWindows,
                        IncludePrivateKeyMaterial = document.Network.RoamWiredDot1xPrivateKeyMaterialToWindows
                    },
                    Wifi = new NetworkProfileRoamingTransportSettings
                    {
                        IsEnabled = document.Network.RoamWifiProfileToWindows,
                        IncludePrivateKeyMaterial = document.Network.RoamWifiPrivateKeyMaterialToWindows
                    },
                    ArtifactRootPath = NetworkProfileRoamingArtifacts.DefaultArtifactRootPath
                }
            },
            Customization = new DeployCustomizationSettings
            {
                MachineNaming = new DeployMachineNamingSettings
                {
                    IsEnabled = document.Customization.MachineNaming.IsEnabled,
                    Mode = document.Customization.MachineNaming.IsEnabled
                        ? document.Customization.MachineNaming.Mode
                        : MachineNamingMode.Manual,
                    ManualInitialValue = document.Customization.MachineNaming.IsEnabled &&
                                         document.Customization.MachineNaming.Mode == MachineNamingMode.Manual
                        ? document.Customization.MachineNaming.ManualInitialValue
                        : null,
                    Components = document.Customization.MachineNaming.IsEnabled &&
                                 document.Customization.MachineNaming.Mode == MachineNamingMode.Composed
                        ? document.Customization.MachineNaming.Components.Select(component => new DeployMachineNameComponentSettings
                        {
                            Type = component.Type,
                            StaticText = component.StaticText,
                            MaximumLength = component.MaximumLength,
                            Truncation = component.Truncation
                        }).ToArray()
                        : [],
                    Separator = document.Customization.MachineNaming.Separator,
                    Casing = document.Customization.MachineNaming.Casing,
                    AllowEditingDuringDeployment = document.Customization.MachineNaming.AllowEditingDuringDeployment
                },
                Oobe = MapOobeSettings(document.Customization.Oobe, deploymentSecretsKey, oobeAccountSecretState),
                AppxRemoval = MapAppxRemovalSettings(document.Customization.AppxRemoval),
                WindowsOptionalFeatures = MapWindowsOptionalFeatureSettings(document.Customization.WindowsOptionalFeatures),
                AiComponentRemoval = MapAiComponentRemovalSettings(
                    document.Customization.AiComponentRemoval,
                    document.Customization.AppxRemoval)
            },
            Autopilot = new DeployAutopilotSettings
            {
                IsEnabled = document.Autopilot.IsEnabled,
                ProvisioningMode = document.Autopilot.ProvisioningMode,
                DefaultProfileFolderName = document.Autopilot.ProvisioningMode == AutopilotProvisioningMode.JsonProfile
                    ? document.Autopilot.Profiles
                        .FirstOrDefault(profile => string.Equals(profile.Id, document.Autopilot.DefaultProfileId, StringComparison.OrdinalIgnoreCase))
                        ?.FolderName
                    : null,
                HardwareHashUpload = CreateDeployHardwareHashUploadSettings(
                    document.Autopilot,
                    deploymentSecretsKey)
            },
            Telemetry = document.Telemetry
        };
    }

    /// <inheritdoc />
    public string Serialize(FoundryDeployConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, ConfigurationJsonDefaults.SerializerOptions);
    }

    private static DeployAutopilotHardwareHashUploadSettings CreateDeployHardwareHashUploadSettings(
        AutopilotSettings autopilot,
        byte[]? deploymentSecretsKey)
    {
        if (!autopilot.IsEnabled ||
            autopilot.ProvisioningMode != AutopilotProvisioningMode.HardwareHashUpload)
        {
            return new DeployAutopilotHardwareHashUploadSettings();
        }

        AutopilotHardwareHashUploadSettings? settings = autopilot.HardwareHashUpload;
        if (settings?.Tenant is null)
        {
            return new DeployAutopilotHardwareHashUploadSettings();
        }

        SecretEnvelope? pfxSecret = null;
        SecretEnvelope? pfxPasswordSecret = null;
        if (deploymentSecretsKey is not null)
        {
            if (deploymentSecretsKey.Length == 0)
            {
                throw new InvalidOperationException("Autopilot hardware hash upload media generation requires a Deploy secret key.");
            }

            AutopilotBootMediaCertificateSettings bootMediaCertificate = settings.BootMediaCertificate;
            if (string.IsNullOrWhiteSpace(bootMediaCertificate.PfxPath) ||
                !File.Exists(bootMediaCertificate.PfxPath))
            {
                throw new InvalidOperationException("Autopilot hardware hash upload media generation requires the selected PFX file.");
            }

            if (string.IsNullOrWhiteSpace(bootMediaCertificate.PfxPassword))
            {
                throw new InvalidOperationException("Autopilot hardware hash upload media generation requires the selected PFX password.");
            }

            byte[] pfxBytes = File.ReadAllBytes(bootMediaCertificate.PfxPath);
            try
            {
                pfxSecret = MediaSecretEnvelopeProtector.EncryptBytes(
                    pfxBytes,
                    deploymentSecretsKey,
                    MediaSecretEnvelopeProtector.DeploymentKeyId);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(pfxBytes);
            }

            pfxPasswordSecret = MediaSecretEnvelopeProtector.EncryptString(
                bootMediaCertificate.PfxPassword,
                deploymentSecretsKey,
                MediaSecretEnvelopeProtector.DeploymentKeyId);
        }

        return new DeployAutopilotHardwareHashUploadSettings
        {
            TenantId = settings.Tenant.TenantId,
            ClientId = settings.Tenant.ClientId,
            ActiveCertificateKeyId = settings.ActiveCertificate?.KeyId,
            ActiveCertificateThumbprint = settings.ActiveCertificate?.Thumbprint,
            ActiveCertificateExpiresOnUtc = settings.ActiveCertificate?.ExpiresOnUtc,
            DefaultGroupTag = NormalizeOptionalGroupTag(settings.DefaultGroupTag),
            CertificatePfxSecret = pfxSecret,
            CertificatePfxPasswordSecret = pfxPasswordSecret
        };
    }

    private static string? NormalizeOptionalGroupTag(string? groupTag)
    {
        string? trimmed = groupTag?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static void ThrowIfAutopilotConflictsWithOobeAccounts(AutopilotSettings autopilot, OobeSettings oobe)
    {
        if (!autopilot.IsEnabled || !oobe.IsEnabled)
        {
            return;
        }

        if (oobe.AdditionalAccounts.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException("Autopilot cannot be combined with additional OOBE local accounts.");
    }

    private static void ThrowIfOobePasswordsRequireProtectedMedia(
        OobeSettings settings,
        DeployProtectionSettings? protectionSettings,
        byte[]? deploymentSecretsKey)
    {
        if (!OobeAccountConfigurationValidator.RequiresProtectedMedia(settings))
        {
            return;
        }

        if (protectionSettings?.IsEnabled != true)
        {
            throw new InvalidOperationException("Protected deployment media is required when OOBE local account passwords are configured.");
        }

        if (deploymentSecretsKey is not { Length: > 0 })
        {
            throw new InvalidOperationException("OOBE local account password generation requires a Deploy secret key.");
        }
    }

    private static DeployOobeSettings MapOobeSettings(
        OobeSettings settings,
        byte[]? deploymentSecretsKey,
        OobeAccountSecretState? oobeAccountSecretState)
    {
        if (!settings.IsEnabled)
        {
            return new DeployOobeSettings();
        }

        (bool administratorPasswordIsBlank, SecretEnvelope? administratorPasswordSecret) =
            CreateAccountPassword(
                settings.EnableAdministratorAccount,
                settings.UseAdministratorPassword,
                deploymentSecretsKey,
                oobeAccountSecretState?.GetAdministratorPasswordCopy());

        return new DeployOobeSettings
        {
            IsEnabled = true,
            EnableAdministratorAccount = settings.EnableAdministratorAccount,
            SkipLicenseTerms = settings.SkipLicenseTerms,
            DiagnosticDataLevel = MapDiagnosticDataLevel(settings.DiagnosticDataLevel),
            HidePrivacySetup = settings.HidePrivacySetup,
            AllowTailoredExperiences = settings.AllowTailoredExperiences,
            AllowAdvertisingId = settings.AllowAdvertisingId,
            AllowOnlineSpeechRecognition = settings.AllowOnlineSpeechRecognition,
            AllowInkingAndTypingDiagnostics = settings.AllowInkingAndTypingDiagnostics,
            LocationAccess = MapLocationAccess(settings.LocationAccess),
            AdministratorPasswordIsBlank = administratorPasswordIsBlank,
            AdministratorPasswordSecret = administratorPasswordSecret,
            AdditionalAccounts = settings.AdditionalAccounts
                .Select(account => CreateAdditionalAccount(account, deploymentSecretsKey, oobeAccountSecretState))
                .ToArray()
        };
    }

    private static DeployOobeAdditionalAccountSettings CreateAdditionalAccount(
        OobeAdditionalAccountSettings account,
        byte[]? deploymentSecretsKey,
        OobeAccountSecretState? oobeAccountSecretState)
    {
        (bool passwordIsBlank, SecretEnvelope? passwordSecret) =
            CreateAccountPassword(
                isProvisioned: true,
                usePassword: account.UsePassword,
                deploymentSecretsKey,
                oobeAccountSecretState?.GetAdditionalAccountPasswordCopy(account.Id));

        return new DeployOobeAdditionalAccountSettings
        {
            Id = account.Id,
            UserName = account.UserName,
            Type = account.Type,
            PasswordIsBlank = passwordIsBlank,
            PasswordSecret = passwordSecret
        };
    }

    private static (bool IsBlank, SecretEnvelope? Secret) CreateAccountPassword(
        bool isProvisioned,
        bool usePassword,
        byte[]? deploymentSecretsKey,
        char[]? password)
    {
        if (!isProvisioned)
        {
            if (password is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
            }

            return (false, null);
        }

        if (!usePassword)
        {
            if (password is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
            }

            return (true, null);
        }

        char[] effectivePassword = password ?? [];
        try
        {
            if (effectivePassword.Length == 0)
            {
                throw new InvalidOperationException("An OOBE account password was configured but is unavailable in the current session.");
            }

            byte[] plaintextBytes = Encoding.UTF8.GetBytes(effectivePassword);
            try
            {
                return (
                    false,
                    MediaSecretEnvelopeProtector.EncryptBytes(
                        plaintextBytes,
                        deploymentSecretsKey!,
                        MediaSecretEnvelopeProtector.DeploymentKeyId));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(effectivePassword.AsSpan()));
        }
    }

    private static DeployOobeDiagnosticDataLevel MapDiagnosticDataLevel(OobeDiagnosticDataLevel value)
    {
        return value switch
        {
            OobeDiagnosticDataLevel.Optional => DeployOobeDiagnosticDataLevel.Optional,
            OobeDiagnosticDataLevel.Off => DeployOobeDiagnosticDataLevel.Off,
            _ => DeployOobeDiagnosticDataLevel.Required
        };
    }

    private static DeployOobeLocationAccessMode MapLocationAccess(OobeLocationAccessMode value)
    {
        return value == OobeLocationAccessMode.ForceOff
            ? DeployOobeLocationAccessMode.ForceOff
            : DeployOobeLocationAccessMode.UserControlled;
    }

    private static DeployAppxRemovalSettings MapAppxRemovalSettings(AppxRemovalSettings settings)
    {
        string[] packageNames = CanonicalizePackageNames(settings.PackageNames);
        return settings.IsEnabled && packageNames.Length > 0
            ? new DeployAppxRemovalSettings
            {
                IsEnabled = true,
                PackageNames = packageNames
            }
            : new DeployAppxRemovalSettings();
    }

    private static DeployWindowsOptionalFeatureSettings MapWindowsOptionalFeatureSettings(WindowsOptionalFeatureSettings settings)
    {
        WindowsOptionalFeatureSettings normalized = WindowsOptionalFeatureSettingsNormalizer.Normalize(settings);
        HashSet<string> enabledIds = normalized.EnabledFeatureIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> disabledIds = normalized.DisabledFeatureIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        DeployWindowsOptionalFeatureAction[] actions = WindowsOptionalFeatureCatalog.Entries
            .Where(entry => enabledIds.Contains(entry.Id) || disabledIds.Contains(entry.Id))
            .Select(entry => new DeployWindowsOptionalFeatureAction
            {
                Id = entry.Id,
                Enable = enabledIds.Contains(entry.Id)
            })
            .ToArray();

        return normalized.IsEnabled && actions.Length > 0
            ? new DeployWindowsOptionalFeatureSettings
            {
                IsEnabled = true,
                Actions = actions
            }
            : new DeployWindowsOptionalFeatureSettings();
    }

    private static DeployAiComponentRemovalSettings MapAiComponentRemovalSettings(
        AiComponentRemovalSettings settings,
        AppxRemovalSettings legacyAppxRemoval)
    {
        bool removeCopilot = settings.IsEnabled && settings.RemoveCopilot ||
            HasLegacyAppxRemovalPackage(legacyAppxRemoval, "Microsoft.Copilot");
        bool removeAiHub = settings.IsEnabled && settings.RemoveAiHub ||
            HasLegacyAppxRemovalPackage(legacyAppxRemoval, "Microsoft.Windows.AIHub");
        bool isEnabled = settings.IsEnabled || removeCopilot || removeAiHub;
        var effectiveSettings = new AiComponentRemovalSettings
        {
            IsEnabled = isEnabled,
            RemoveCopilot = removeCopilot,
            RemoveAiHub = removeAiHub,
            DisableRecall = settings.IsEnabled && settings.DisableRecall,
            DisableClickToDo = settings.IsEnabled && settings.DisableClickToDo,
            DisableAiServiceAutoStart = settings.IsEnabled && settings.DisableAiServiceAutoStart,
            DisableEdgeAi = settings.IsEnabled && settings.DisableEdgeAi,
            DisablePaintAi = settings.IsEnabled && settings.DisablePaintAi,
            DisableNotepadAi = settings.IsEnabled && settings.DisableNotepadAi
        };

        if (!effectiveSettings.IsEnabled || !effectiveSettings.HasAnyAction())
        {
            return new DeployAiComponentRemovalSettings();
        }

        return new DeployAiComponentRemovalSettings
        {
            IsEnabled = true,
            RemoveCopilot = effectiveSettings.RemoveCopilot,
            RemoveAiHub = effectiveSettings.RemoveAiHub,
            DisableRecall = effectiveSettings.DisableRecall,
            DisableClickToDo = effectiveSettings.DisableClickToDo,
            DisableAiServiceAutoStart = effectiveSettings.DisableAiServiceAutoStart,
            DisableEdgeAi = effectiveSettings.DisableEdgeAi,
            DisablePaintAi = effectiveSettings.DisablePaintAi,
            DisableNotepadAi = effectiveSettings.DisableNotepadAi
        };
    }

    private static bool HasLegacyAppxRemovalPackage(AppxRemovalSettings settings, string packageName)
    {
        return settings.IsEnabled &&
            settings.PackageNames.Any(value => string.Equals(value.Trim(), packageName, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] CanonicalizePackageNames(IEnumerable<string> packageNames)
    {
        ArgumentNullException.ThrowIfNull(packageNames);

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string packageName in packageNames)
        {
            string trimmed = packageName.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                !AppxRemovalCatalog.ContainsPackageName(trimmed) ||
                !seen.Add(trimmed))
            {
                continue;
            }

            result.Add(trimmed);
        }

        return result.ToArray();
    }
}
