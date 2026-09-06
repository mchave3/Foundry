// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Telemetry;

namespace Foundry.Core.Models.Configuration.Deploy;

/// <summary>
/// Represents the configuration consumed by Foundry.Deploy while applying Windows.
/// </summary>
public sealed record FoundryDeployConfigurationDocument
{
    /// <summary>
    /// Gets the current schema version for Foundry.Deploy configuration documents.
    /// </summary>
    public const int CurrentSchemaVersion = ConfigurationSchemaVersions.DeployCurrent;

    /// <summary>
    /// Gets the schema version of this deployment configuration document.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Gets deployment media password-protection metadata.
    /// </summary>
    public DeployProtectionSettings Protection { get; init; } = new();

    /// <summary>
    /// Gets successful deployment completion behavior.
    /// </summary>
    public DeployCompletionSettings Completion { get; init; } = new();

    /// <summary>
    /// Gets Windows catalog selection settings used during apply image selection.
    /// </summary>
    public DeployOperatingSystemSelectionSettings OperatingSystemSelection { get; init; } = new();

    /// <summary>
    /// Gets Windows localization settings used during apply and unattend generation.
    /// </summary>
    public DeployLocalizationSettings Localization { get; init; } = new();

    /// <summary>
    /// Gets network profile roaming settings used during deployment.
    /// </summary>
    public DeployNetworkSettings Network { get; init; } = new();

    /// <summary>
    /// Gets Windows customization settings applied during deployment.
    /// </summary>
    public DeployCustomizationSettings Customization { get; init; } = new();

    /// <summary>
    /// Gets the manifest of protected custom answer-file choices.
    /// </summary>
    public DeployUnattendSettings Unattend { get; init; } = new();

    /// <summary>
    /// Gets Autopilot provisioning settings.
    /// </summary>
    public DeployAutopilotSettings Autopilot { get; init; } = new();

    /// <summary>
    /// Gets telemetry policy and runtime settings consumed by Foundry.Deploy.
    /// </summary>
    public TelemetrySettings Telemetry { get; init; } = new();
}
