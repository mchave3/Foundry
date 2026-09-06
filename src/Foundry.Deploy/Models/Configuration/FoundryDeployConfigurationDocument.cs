// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Telemetry;
using CoreDeployNetworkSettings = Foundry.Core.Models.Configuration.Deploy.DeployNetworkSettings;

namespace Foundry.Deploy.Models.Configuration;

/// <summary>
/// Represents the runtime configuration consumed by Foundry.Deploy inside WinPE.
/// </summary>
public sealed record FoundryDeployConfigurationDocument
{
    /// <summary>
    /// Gets the current configuration schema version.
    /// </summary>
    public const int CurrentSchemaVersion = ConfigurationSchemaVersions.DeployCurrent;

    /// <summary>
    /// Gets the schema version of this configuration.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Gets the protected answer-file manifest without authoring paths or XML content.
    /// </summary>
    public Foundry.Core.Models.Configuration.Deploy.DeployUnattendSettings Unattend { get; init; } = new();

    /// <summary>Gets deployment media access-protection settings.</summary>
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
    /// Gets Windows localization settings used during deployment.
    /// </summary>
    public DeployLocalizationSettings Localization { get; init; } = new();

    /// <summary>
    /// Gets network profile roaming settings used during deployment.
    /// </summary>
    public CoreDeployNetworkSettings Network { get; init; } = new();

    /// <summary>
    /// Gets Windows customization settings applied during deployment.
    /// </summary>
    public DeployCustomizationSettings Customization { get; init; } = new();

    /// <summary>
    /// Gets Autopilot provisioning settings.
    /// </summary>
    public DeployAutopilotSettings Autopilot { get; init; } = new();

    /// <summary>
    /// Gets telemetry policy and runtime settings for Foundry.Deploy.
    /// </summary>
    public TelemetrySettings Telemetry { get; init; } = new();
}
