// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Core.Services.Configuration;
using Foundry.Telemetry;

namespace Foundry.Services.Configuration;

/// <summary>
/// Owns the mutable Foundry configuration assembled by the Foundry UI.
/// </summary>
/// <remarks>
/// <see cref="Current"/> is always safe to persist. Volatile network secrets are kept outside the document and
/// merged only when <see cref="GenerateConnectProvisioningBundle"/> creates the Connect payload.
/// </remarks>
public interface IFoundryConfigurationStateService
{
    /// <summary>
    /// Occurs after the Foundry configuration changes.
    /// </summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Gets the current Foundry configuration document after removing values that must not be persisted.
    /// </summary>
    FoundryConfigurationDocument Current { get; }

    /// <summary>
    /// Gets the detailed network readiness evaluation used for media creation.
    /// </summary>
    NetworkMediaReadinessEvaluation NetworkMediaReadiness { get; }

    /// <summary>
    /// Gets a value indicating whether network configuration can be emitted for Connect.
    /// </summary>
    bool IsNetworkConfigurationReady { get; }

    /// <summary>
    /// Gets a value indicating whether Deploy configuration can be emitted.
    /// </summary>
    bool IsDeployConfigurationReady { get; }

    /// <summary>
    /// Gets a value indicating whether Connect provisioning files can be generated.
    /// </summary>
    bool IsConnectProvisioningReady { get; }

    /// <summary>
    /// Gets a value indicating whether required in-memory secrets are available.
    /// </summary>
    bool AreRequiredSecretsReady { get; }

    /// <summary>
    /// Gets a value indicating whether Autopilot provisioning is enabled.
    /// </summary>
    bool IsAutopilotEnabled { get; }

    /// <summary>
    /// Gets a value indicating whether the selected Autopilot profiles are valid for output.
    /// </summary>
    bool IsAutopilotConfigurationReady { get; }

    /// <summary>
    /// Gets whether custom answer-file sources and media protection are ready for generation.
    /// </summary>
    bool IsUnattendConfigurationReady { get; }

    /// <summary>
    /// Gets cached source inspections for the current catalog, without performing file I/O.
    /// </summary>
    IReadOnlyList<UnattendSourceValidation> UnattendSourceValidations { get; }

    /// <summary>
    /// Refreshes bounded source inspections off the UI thread and publishes current results through StateChanged.
    /// Late results from replaced catalogs are discarded; inaccessible sources never block the UI thread.
    /// </summary>
    Task RefreshUnattendSourcesAsync();

    /// <summary>
    /// Gets the detailed Autopilot readiness status for the selected provisioning mode.
    /// </summary>
    AutopilotConfigurationValidationResult AutopilotConfigurationValidation { get; }

    /// <summary>
    /// Gets the selected Autopilot provisioning mode.
    /// </summary>
    AutopilotProvisioningMode AutopilotProvisioningMode { get; }

    /// <summary>
    /// Gets the selected Autopilot profile display name when a single profile is selected.
    /// </summary>
    string? SelectedAutopilotProfileDisplayName { get; }

    /// <summary>
    /// Gets the selected Autopilot profile folder name when a single profile is selected.
    /// </summary>
    string? SelectedAutopilotProfileFolderName { get; }

    /// <summary>
    /// Replaces the general boot media authoring configuration section.
    /// </summary>
    /// <param name="settings">New general settings.</param>
    void UpdateGeneral(GeneralSettings settings);

    /// <summary>
    /// Replaces the network configuration section and stores required secrets in volatile state.
    /// </summary>
    /// <param name="settings">New network settings.</param>
    void UpdateNetwork(NetworkSettings settings);

    /// <summary>
    /// Replaces the OS catalog selection configuration section.
    /// </summary>
    /// <param name="settings">New OS catalog selection settings.</param>
    void UpdateOperatingSystemSelection(OperatingSystemSelectionSettings settings);

    /// <summary>
    /// Replaces the localization configuration section.
    /// </summary>
    /// <param name="settings">New localization settings.</param>
    void UpdateLocalization(LocalizationSettings settings);

    /// <summary>
    /// Replaces the customization configuration section.
    /// </summary>
    /// <param name="settings">New customization settings.</param>
    void UpdateCustomization(CustomizationSettings settings);

    /// <summary>
    /// Replaces the Autopilot configuration section.
    /// </summary>
    /// <param name="settings">New Autopilot settings.</param>
    void UpdateAutopilot(AutopilotSettings settings);

    /// <summary>
    /// Persists answer-file metadata and source references without storing XML content.
    /// </summary>
    /// <param name="settings">New answer-file settings.</param>
    void UpdateUnattend(UnattendSettings settings);

    /// <summary>
    /// Replaces telemetry settings propagated into generated runtime configuration.
    /// </summary>
    /// <param name="settings">New telemetry settings.</param>
    void UpdateTelemetry(TelemetrySettings settings);

    /// <summary>
    /// Generates Connect provisioning files after merging required volatile secrets back into the current network settings.
    /// </summary>
    /// <param name="stagingDirectoryPath">Directory where provisioning files should be staged.</param>
    /// <param name="telemetryOverride">Optional runtime telemetry settings used only for the generated Connect document.</param>
    /// <returns>The generated Connect provisioning bundle.</returns>
    FoundryConnectProvisioningBundle GenerateConnectProvisioningBundle(string stagingDirectoryPath, TelemetrySettings? telemetryOverride = null);

    /// <summary>
    /// Generates the Deploy configuration JSON for the current Foundry configuration.
    /// </summary>
    /// <param name="telemetryOverride">Optional runtime telemetry settings used only for the generated Deploy document.</param>
    /// <param name="deploymentSecretsKey">Optional Deploy secret key used for boot-media-only Deploy secrets.</param>
    /// <param name="protectionSettings">Optional deployment media protection metadata.</param>
    /// <returns>Serialized Deploy configuration JSON.</returns>
    string GenerateDeployConfigurationJson(
        TelemetrySettings? telemetryOverride = null,
        byte[]? deploymentSecretsKey = null,
        DeployProtectionSettings? protectionSettings = null);
}
