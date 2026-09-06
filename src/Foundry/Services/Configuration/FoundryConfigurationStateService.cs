// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Core.Services.Configuration;
using Foundry.Core.Services.WinPe;
using Foundry.Utilities.IO;
using Foundry.Services.Autopilot;
using Foundry.Telemetry;
using Foundry.Utilities.Security;
using Serilog;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AppSettingsService = Foundry.Services.Settings.IAppSettingsService;

namespace Foundry.Services.Configuration;

/// <summary>
/// Maintains the user-facing Foundry configuration state and generates deploy/connect payloads from it.
/// </summary>
/// <remarks>
/// Secrets that should not be persisted are kept in <see cref="INetworkSecretStateService"/> and
/// <see cref="IOobeAccountSecretStateService"/> and are only merged when a provisioning bundle is generated.
/// </remarks>
internal sealed class FoundryConfigurationStateService : IFoundryConfigurationStateService
{
    private readonly IFoundryConfigurationService foundryConfigurationService;
    private readonly IDeployConfigurationGenerator deployConfigurationGenerator;
    private readonly IConnectConfigurationGenerator connectConfigurationGenerator;
    private readonly INetworkSecretStateService networkSecretStateService;
    private readonly IDeploymentProtectionSecretStateService deploymentProtectionSecretStateService;
    private readonly IOobeAccountSecretStateService oobeAccountSecretStateService;
    private readonly IAutopilotHardwareHashSessionState autopilotHardwareHashSessionState;
    private readonly AppSettingsService appSettingsService;
    private readonly ILogger logger;
    private readonly object stateLock = new();
    private UnattendSettings? validatedUnattendSettings;
    private IReadOnlyList<UnattendSourceValidation> unattendSourceValidations = [];
    private readonly List<(UnattendSettings Settings, Task<IReadOnlyList<UnattendSourceValidation>> Read)> unattendSourceReads = [];
    private long unattendRefreshRevision;

    public FoundryConfigurationStateService(
        IFoundryConfigurationService foundryConfigurationService,
        IDeployConfigurationGenerator deployConfigurationGenerator,
        IConnectConfigurationGenerator connectConfigurationGenerator,
        INetworkSecretStateService networkSecretStateService,
        IDeploymentProtectionSecretStateService deploymentProtectionSecretStateService,
        IOobeAccountSecretStateService oobeAccountSecretStateService,
        IAutopilotHardwareHashSessionState autopilotHardwareHashSessionState,
        AppSettingsService appSettingsService,
        ILogger logger)
    {
        this.foundryConfigurationService = foundryConfigurationService;
        this.deployConfigurationGenerator = deployConfigurationGenerator;
        this.connectConfigurationGenerator = connectConfigurationGenerator;
        this.networkSecretStateService = networkSecretStateService;
        this.deploymentProtectionSecretStateService = deploymentProtectionSecretStateService;
        this.oobeAccountSecretStateService = oobeAccountSecretStateService;
        this.autopilotHardwareHashSessionState = autopilotHardwareHashSessionState;
        this.appSettingsService = appSettingsService;
        this.logger = logger.ForContext<FoundryConfigurationStateService>();
        FoundryConfigurationDocument initial = SanitizeForPersistence(Load());
        Save(initial);
        Current = initial;
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public FoundryConfigurationDocument Current { get; private set; }

    /// <inheritdoc />
    public NetworkMediaReadinessEvaluation NetworkMediaReadiness => EvaluateNetworkMediaReadiness();

    /// <inheritdoc />
    public bool IsNetworkConfigurationReady => NetworkMediaReadiness.IsNetworkConfigurationReady;

    /// <inheritdoc />
    public bool IsDeployConfigurationReady
    {
        get
        {
            if (!IsUnattendConfigurationReady)
            {
                return false;
            }

            if (Current.General.DeploymentProtection.IsEnabled &&
                !deploymentProtectionSecretStateService.IsValid)
            {
                return false;
            }

            if (!oobeAccountSecretStateService.Validate(Current.Customization.Oobe).IsValid)
            {
                return false;
            }

            try
            {
                byte[]? deploymentSecretsKey = Current.General.DeploymentProtection.IsEnabled ||
                                               Current.Autopilot.IsEnabled &&
                                               Current.Autopilot.ProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload
                    ? AesGcmEncryption.GenerateKey()
                    : null;
                DeployProtectionSettings? protectionSettings = Current.General.DeploymentProtection.IsEnabled
                    ? new DeployProtectionSettings
                    {
                        IsEnabled = true
                    }
                    : null;

                try
                {
                    _ = GenerateDeployConfigurationJson(
                        deploymentSecretsKey: deploymentSecretsKey,
                        protectionSettings: protectionSettings);
                    return true;
                }
                finally
                {
                    if (deploymentSecretsKey is not null)
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(deploymentSecretsKey);
                    }
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool IsConnectProvisioningReady => NetworkMediaReadiness.IsConnectProvisioningReady;

    /// <inheritdoc />
    public bool AreRequiredSecretsReady => NetworkMediaReadiness.AreRequiredSecretsReady;

    /// <inheritdoc />
    public bool IsAutopilotEnabled => Current.Autopilot.IsEnabled;

    /// <inheritdoc />
    public bool IsAutopilotConfigurationReady => AutopilotConfigurationValidation.IsReady;

    /// <inheritdoc />
    public bool IsUnattendConfigurationReady
    {
        get
        {
            try
            {
                UnattendFileService.ValidateSettings(Current.Unattend, Current.General.DeploymentProtection.IsEnabled);
                return !Current.Unattend.IsEnabled ||
                    (ReferenceEquals(validatedUnattendSettings, Current.Unattend) &&
                     unattendSourceValidations.Count == Current.Unattend.Files.Count &&
                     unattendSourceValidations.All(result => result.Inspection is not null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UnattendSourceValidation> UnattendSourceValidations =>
        ReferenceEquals(validatedUnattendSettings, Current.Unattend) ? unattendSourceValidations : [];

    /// <inheritdoc />
    public async Task RefreshUnattendSourcesAsync()
    {
        UnattendSettings settings = Current.Unattend;
        long revision = ++unattendRefreshRevision;
        validatedUnattendSettings = null;
        StateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            if (!ReferenceEquals(Current.Unattend, settings))
            {
                return;
            }

            // Keep one replacement slot so removing a hung source can recover without spawning unlimited workers.
            unattendSourceReads.RemoveAll(scan => scan.Read.IsCompleted);
            Task<IReadOnlyList<UnattendSourceValidation>>? sourceRead = unattendSourceReads
                .FirstOrDefault(scan => ReferenceEquals(scan.Settings, settings)).Read;
            if (sourceRead is null)
            {
                if (unattendSourceReads.Count >= 2)
                {
                    PublishUnattendSourceResults(settings, revision, (settings.Files ?? []).Select(file => new UnattendSourceValidation(
                        file, null, "Two source checks are still waiting for file access. Restore the unavailable source locations, then check sources again.")).ToArray());
                    return;
                }

                sourceRead = Task.Run(() => InspectUnattendSources(settings));
                unattendSourceReads.Add((settings, sourceRead));
            }

            IReadOnlyList<UnattendSourceValidation> results = await sourceRead.WaitAsync(TimeSpan.FromSeconds(15));
            PublishUnattendSourceResults(settings, revision, results);
        }
        catch (TimeoutException)
        {
            PublishUnattendSourceResults(settings, revision, (settings.Files ?? []).Select(file => new UnattendSourceValidation(
                file, null, "Source validation timed out. Check the source location and refresh again.")).ToArray());
        }
    }

    private void PublishUnattendSourceResults(UnattendSettings settings, long revision, IReadOnlyList<UnattendSourceValidation> results)
    {
        if (revision != unattendRefreshRevision || !ReferenceEquals(Current.Unattend, settings))
        {
            return;
        }

        validatedUnattendSettings = settings;
        unattendSourceValidations = results;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyList<UnattendSourceValidation> InspectUnattendSources(UnattendSettings settings)
    {
        List<UnattendSourceValidation> results = [];
        foreach (UnattendFileSettings file in settings.Files ?? [])
        {
            byte[]? content = null;
            try
            {
                content = UnattendFileService.ReadValidated(file);
                results.Add(new UnattendSourceValidation(file, UnattendFileService.Inspect(content), null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                string message = ex is InvalidDataException or InvalidOperationException
                    ? ex.Message
                    : "The answer-file source could not be read. Check its location and access permissions.";
                results.Add(new UnattendSourceValidation(file, null, message));
            }
            finally
            {
                if (content is not null)
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }
        }

        return results;
    }

    /// <inheritdoc />
    public AutopilotConfigurationValidationResult AutopilotConfigurationValidation =>
        AutopilotConfigurationValidator.Evaluate(CreateAutopilotSettingsForValidation(Current.Autopilot), DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public AutopilotProvisioningMode AutopilotProvisioningMode => Current.Autopilot.ProvisioningMode;

    /// <inheritdoc />
    public string? SelectedAutopilotProfileDisplayName => Current.Autopilot.IsEnabled &&
                                                          Current.Autopilot.ProvisioningMode == AutopilotProvisioningMode.JsonProfile
        ? GetSelectedAutopilotProfile()?.DisplayName
        : null;

    /// <inheritdoc />
    public string? SelectedAutopilotProfileFolderName => Current.Autopilot.IsEnabled &&
                                                         Current.Autopilot.ProvisioningMode == AutopilotProvisioningMode.JsonProfile
        ? GetSelectedAutopilotProfile()?.FolderName
        : null;

    /// <inheritdoc />
    public void UpdateGeneral(GeneralSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UpdateAndPersist(current => current with { General = SanitizeGeneralForPersistence(settings) });
    }

    /// <inheritdoc />
    public void UpdateLocalization(LocalizationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UpdateAndPersist(current => current with { Localization = settings });
    }

    /// <inheritdoc />
    public void UpdateOperatingSystemSelection(OperatingSystemSelectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UpdateAndPersist(current => current with
        {
            OperatingSystemSelection = OperatingSystemSelectionSettingsNormalizer.Normalize(settings)
        });
    }

    /// <inheritdoc />
    public void UpdateNetwork(NetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UpdateAndPersist(
            current => current with { Network = NetworkConfigurationValidator.SanitizeForPersistence(settings) },
            () => networkSecretStateService.Update(settings));
    }

    /// <inheritdoc />
    public void UpdateCustomization(CustomizationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UpdateAndPersist(
            current => current with { Customization = SanitizeCustomizationForPersistence(settings) },
            () => oobeAccountSecretStateService.Update(settings.Oobe));
    }

    /// <inheritdoc />
    public void UpdateAutopilot(AutopilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UpdateAndPersist(current => current with { Autopilot = SanitizeAutopilotForPersistence(settings) });
    }

    /// <inheritdoc />
    public void UpdateUnattend(UnattendSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UpdateAndPersist(current => current with { Unattend = settings });
    }

    /// <inheritdoc />
    public void UpdateTelemetry(TelemetrySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UpdateAndPersist(current => current with { Telemetry = settings });
    }

    /// <inheritdoc />
    public string GenerateDeployConfigurationJson(
        TelemetrySettings? telemetryOverride = null,
        byte[]? deploymentSecretsKey = null,
        DeployProtectionSettings? protectionSettings = null)
    {
        FoundryConfigurationDocument document = CreateDocumentForDeployGeneration(telemetryOverride);
        OobeAccountConfigurationValidationResult validation = oobeAccountSecretStateService.Validate(document.Customization.Oobe);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("OOBE local account password confirmation is invalid.");
        }

        using OobeAccountSecretState oobeAccountSecretState = CreateOobeAccountSecretStateForDeployGeneration(document.Customization.Oobe);

        return deployConfigurationGenerator.Serialize(
            deployConfigurationGenerator.Generate(document, deploymentSecretsKey, protectionSettings, oobeAccountSecretState));
    }

    /// <inheritdoc />
    public FoundryConnectProvisioningBundle GenerateConnectProvisioningBundle(string stagingDirectoryPath, TelemetrySettings? telemetryOverride = null)
    {
        FoundryConfigurationDocument document = Current with
        {
            Network = networkSecretStateService.ApplyRequiredSecrets(Current.Network),
            Telemetry = telemetryOverride ?? Current.Telemetry
        };

        return connectConfigurationGenerator.CreateProvisioningBundle(document, stagingDirectoryPath);
    }

    private FoundryConfigurationDocument Load()
    {
        if (!File.Exists(Constants.FoundryConfigurationStatePath))
        {
            logger.Information("Foundry configuration state initialized from defaults.");
            return FoundryConfigurationMigration.ApplyLegacyGeneralSettings(
                CreateDefaultDocument(),
                appSettingsService.MigratedGeneralSettings);
        }

        try
        {
            string json = File.ReadAllText(Constants.FoundryConfigurationStatePath);
            FoundryConfigurationDocument document = foundryConfigurationService.Deserialize(json);
            logger.Information("Foundry configuration state loaded from disk.");
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            string backupPath = CreateInvalidBackupPath(Constants.FoundryConfigurationStatePath);
            TryMoveInvalidState(backupPath, ex);
            return CreateDefaultDocument();
        }
    }

    private FoundryConfigurationDocument CreateDocumentForDeployGeneration(TelemetrySettings? telemetryOverride)
    {
        return Current with
        {
            Autopilot = CreateAutopilotSettingsForValidation(Current.Autopilot),
            Telemetry = telemetryOverride ?? Current.Telemetry
        };
    }

    private OobeAccountSecretState CreateOobeAccountSecretStateForDeployGeneration(OobeSettings settings)
    {
        var state = new OobeAccountSecretState();

        if (!settings.IsEnabled)
        {
            return state;
        }

        char[] administratorPassword = oobeAccountSecretStateService.GetAdministratorPasswordCopy();
        try
        {
            state.SetAdministratorPassword(administratorPassword);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(administratorPassword.AsSpan()));
        }

        char[] administratorConfirmation = oobeAccountSecretStateService.GetAdministratorConfirmationCopy();
        try
        {
            state.SetAdministratorConfirmation(administratorConfirmation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(administratorConfirmation.AsSpan()));
        }

        foreach (OobeAdditionalAccountSettings account in settings.AdditionalAccounts)
        {
            char[] password = oobeAccountSecretStateService.GetAdditionalAccountPasswordCopy(account.Id);
            try
            {
                state.SetAdditionalAccountPassword(account.Id, password);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
            }

            char[] confirmation = oobeAccountSecretStateService.GetAdditionalAccountConfirmationCopy(account.Id);
            try
            {
                state.SetAdditionalAccountConfirmation(account.Id, confirmation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(confirmation.AsSpan()));
            }
        }

        return state;
    }

    private AutopilotSettings CreateAutopilotSettingsForValidation(AutopilotSettings settings)
    {
        AutopilotHardwareHashUploadSettings hardwareHashUpload = settings.HardwareHashUpload with
        {
            BootMediaCertificate = autopilotHardwareHashSessionState.BootMediaCertificate
        };
        if (settings.ProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload &&
            !autopilotHardwareHashSessionState.HasConnectedTenant)
        {
            hardwareHashUpload = hardwareHashUpload with
            {
                KnownGroupTags = [],
                DefaultGroupTag = null
            };
        }

        return settings with
        {
            HardwareHashUpload = hardwareHashUpload
        };
    }

    private static FoundryConfigurationDocument CreateDefaultDocument()
    {
        return new FoundryConfigurationDocument
        {
            General = new GeneralSettings
            {
                IsoOutputPath = Path.Combine(Constants.IsoWorkspaceDirectoryPath, "Foundry.iso")
            }
        };
    }

    private void UpdateAndPersist(
        Func<FoundryConfigurationDocument, FoundryConfigurationDocument> update,
        Action? afterPersistence = null)
    {
        lock (stateLock)
        {
            FoundryConfigurationDocument next = SanitizeForPersistence(update(Current));
            Save(next);
            Current = next;
            afterPersistence?.Invoke();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Save(FoundryConfigurationDocument document)
    {
        try
        {
            Directory.CreateDirectory(Constants.ConfigurationWorkspaceDirectoryPath);
            string json = foundryConfigurationService.Serialize(document);
            AtomicFile.WriteAllText(Constants.FoundryConfigurationStatePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error(ex, "Failed to persist Foundry configuration state. StatePath={StatePath}", Constants.FoundryConfigurationStatePath);
            throw;
        }
    }

    private static FoundryConfigurationDocument SanitizeForPersistence(FoundryConfigurationDocument document)
    {
        FoundryConfigurationDocument normalized = FoundryConfigurationNormalizer.Normalize(document);
        return normalized with
        {
            General = SanitizeGeneralForPersistence(normalized.General),
            Network = NetworkConfigurationValidator.SanitizeForPersistence(normalized.Network),
            OperatingSystemSelection = OperatingSystemSelectionSettingsNormalizer.Normalize(normalized.OperatingSystemSelection),
            Customization = SanitizeCustomizationForPersistence(normalized.Customization),
            Autopilot = SanitizeAutopilotForPersistence(normalized.Autopilot)
        };
    }

    private static GeneralSettings SanitizeGeneralForPersistence(GeneralSettings settings)
    {
        return settings.Architecture == WinPeArchitecture.Arm64 && settings.UsbPartitionStyle == UsbPartitionStyle.Mbr
            ? settings with { UsbPartitionStyle = UsbPartitionStyle.Gpt }
            : settings;
    }

    private static AutopilotSettings SanitizeAutopilotForPersistence(AutopilotSettings settings)
    {
        // Keep persisted profiles deterministic because the selected profile is referenced by ID across sessions.
        AutopilotProfileSettings[] profiles = settings.Profiles
            .Where(profile =>
                !string.IsNullOrWhiteSpace(profile.Id) &&
                !string.IsNullOrWhiteSpace(profile.DisplayName) &&
                !string.IsNullOrWhiteSpace(profile.FolderName) &&
                !string.IsNullOrWhiteSpace(profile.Source) &&
                !string.IsNullOrWhiteSpace(profile.JsonContent))
            .OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? defaultProfileId = profiles.Any(profile =>
            string.Equals(profile.Id, settings.DefaultProfileId, StringComparison.OrdinalIgnoreCase))
            ? settings.DefaultProfileId
            : profiles.FirstOrDefault()?.Id;

        return settings with
        {
            DefaultProfileId = defaultProfileId,
            Profiles = profiles,
            HardwareHashUpload = SanitizeHardwareHashUploadSettings(settings.HardwareHashUpload)
        };
    }

    private static AutopilotHardwareHashUploadSettings SanitizeHardwareHashUploadSettings(
        AutopilotHardwareHashUploadSettings? settings)
    {
        if (settings?.Tenant is null)
        {
            return new AutopilotHardwareHashUploadSettings();
        }

        string[] knownGroupTags = (settings.KnownGroupTags ?? [])
            .Select(groupTag => groupTag.Trim())
            .Where(groupTag => !string.IsNullOrWhiteSpace(groupTag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(groupTag => groupTag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? defaultGroupTag = NormalizeOptional(settings.DefaultGroupTag);
        if (!string.IsNullOrWhiteSpace(defaultGroupTag) &&
            !knownGroupTags.Contains(defaultGroupTag, StringComparer.OrdinalIgnoreCase))
        {
            defaultGroupTag = null;
        }

        return settings with
        {
            Tenant = new AutopilotTenantRegistrationSettings
            {
                TenantId = NormalizeOptional(settings.Tenant.TenantId),
                ApplicationObjectId = NormalizeOptional(settings.Tenant.ApplicationObjectId),
                ClientId = NormalizeOptional(settings.Tenant.ClientId),
                ServicePrincipalObjectId = NormalizeOptional(settings.Tenant.ServicePrincipalObjectId)
            },
            ActiveCertificate = settings.ActiveCertificate is null
                ? null
                : settings.ActiveCertificate with
                {
                    KeyId = NormalizeOptional(settings.ActiveCertificate.KeyId),
                    Thumbprint = NormalizeOptional(settings.ActiveCertificate.Thumbprint)?.ToUpperInvariant(),
                    DisplayName = NormalizeOptional(settings.ActiveCertificate.DisplayName)
                },
            KnownGroupTags = knownGroupTags,
            DefaultGroupTag = defaultGroupTag
        };
    }

    private static CustomizationSettings SanitizeCustomizationForPersistence(CustomizationSettings settings)
    {
        MachineNamingSettings machineNaming = SanitizeMachineNaming(settings.MachineNaming);

        return settings with
        {
            MachineNaming = machineNaming,
            Oobe = SanitizeOobeForPersistence(settings.Oobe),
            AppxRemoval = SanitizeAppxRemovalForPersistence(settings.AppxRemoval),
            WindowsOptionalFeatures = WindowsOptionalFeatureSettingsNormalizer.Normalize(settings.WindowsOptionalFeatures),
            AiComponentRemoval = SanitizeAiComponentRemovalForPersistence(
                settings.AiComponentRemoval,
                settings.AppxRemoval)
        };
    }

    private static MachineNamingSettings SanitizeMachineNaming(MachineNamingSettings settings)
    {
        if (!settings.IsEnabled)
        {
            return new MachineNamingSettings();
        }

        return settings with
        {
            ManualInitialValue = settings.Mode == MachineNamingMode.Manual
                ? NormalizeOptional(ComputerNameRules.Normalize(settings.ManualInitialValue))
                : null,
            Components = settings.Mode == MachineNamingMode.Composed
                ? settings.Components.Select(component => component with
                {
                    StaticText = component.Type == MachineNameComponentType.StaticText
                        ? ComputerNameRules.Sanitize(component.StaticText)
                        : null
                }).ToArray()
                : []
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        string? trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static OobeSettings SanitizeOobeForPersistence(OobeSettings settings)
    {
        return settings.IsEnabled
            ? settings
            : new OobeSettings();
    }

    private static AppxRemovalSettings SanitizeAppxRemovalForPersistence(AppxRemovalSettings settings)
    {
        string[] packageNames = NormalizeAppxRemovalPackageNames(settings.PackageNames);
        return settings.IsEnabled
            ? new AppxRemovalSettings
            {
                IsEnabled = true,
                PackageNames = packageNames
            }
            : new AppxRemovalSettings();
    }

    private static AiComponentRemovalSettings SanitizeAiComponentRemovalForPersistence(
        AiComponentRemovalSettings settings,
        AppxRemovalSettings legacyAppxRemoval)
    {
        bool removeCopilot = settings.IsEnabled && settings.RemoveCopilot ||
            HasLegacyAppxRemovalPackage(legacyAppxRemoval, "Microsoft.Copilot");
        bool removeAiHub = settings.IsEnabled && settings.RemoveAiHub ||
            HasLegacyAppxRemovalPackage(legacyAppxRemoval, "Microsoft.Windows.AIHub");
        var migratedSettings = new AiComponentRemovalSettings
        {
            IsEnabled = settings.IsEnabled || removeCopilot || removeAiHub,
            RemoveCopilot = removeCopilot,
            RemoveAiHub = removeAiHub,
            DisableRecall = settings.IsEnabled && settings.DisableRecall,
            DisableClickToDo = settings.IsEnabled && settings.DisableClickToDo,
            DisableAiServiceAutoStart = settings.IsEnabled && settings.DisableAiServiceAutoStart,
            DisableEdgeAi = settings.IsEnabled && settings.DisableEdgeAi,
            DisablePaintAi = settings.IsEnabled && settings.DisablePaintAi,
            DisableNotepadAi = settings.IsEnabled && settings.DisableNotepadAi
        };

        return migratedSettings.IsEnabled && migratedSettings.HasAnyAction()
            ? migratedSettings
            : new AiComponentRemovalSettings();
    }

    private static bool HasLegacyAppxRemovalPackage(AppxRemovalSettings settings, string packageName)
    {
        return settings.IsEnabled &&
            settings.PackageNames.Any(value => string.Equals(value.Trim(), packageName, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] NormalizeAppxRemovalPackageNames(IEnumerable<string> packageNames)
    {
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

    private NetworkMediaReadinessEvaluation EvaluateNetworkMediaReadiness()
    {
        return NetworkMediaReadinessEvaluator.Evaluate(Current.Network, networkSecretStateService.PersonalWifiPassphrase);
    }

    private AutopilotProfileSettings? GetSelectedAutopilotProfile()
    {
        if (string.IsNullOrWhiteSpace(Current.Autopilot.DefaultProfileId))
        {
            return null;
        }

        return Current.Autopilot.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, Current.Autopilot.DefaultProfileId, StringComparison.OrdinalIgnoreCase));
    }

    private void TryMoveInvalidState(string backupPath, Exception originalException)
    {
        try
        {
            File.Move(Constants.FoundryConfigurationStatePath, backupPath);
            logger.Warning(
                originalException,
                "Foundry configuration state was invalid and defaults were restored. StatePath={StatePath}, BackupPath={BackupPath}",
                Constants.FoundryConfigurationStatePath,
                backupPath);
        }
        catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException)
        {
            logger.Error(
                backupException,
                "Failed to back up invalid Foundry configuration state. StatePath={StatePath}, BackupPath={BackupPath}, OriginalError={OriginalError}",
                Constants.FoundryConfigurationStatePath,
                backupPath,
                originalException.Message);
            throw;
        }
    }

    private static string CreateInvalidBackupPath(string sourcePath)
    {
        string firstBackupPath = sourcePath + ".invalid";
        return !File.Exists(firstBackupPath)
            ? firstBackupPath
            : $"{firstBackupPath}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}";
    }
}
