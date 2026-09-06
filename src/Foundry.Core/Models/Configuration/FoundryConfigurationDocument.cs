// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Telemetry;

namespace Foundry.Core.Models.Configuration;

/// <summary>
/// Represents the complete user-authored Foundry configuration persisted by the Foundry app.
/// </summary>
public sealed record FoundryConfigurationDocument
{
    /// <summary>
    /// Gets the current schema version for Foundry configuration documents.
    /// </summary>
    public const int CurrentSchemaVersion = ConfigurationSchemaVersions.FoundryCurrent;

    /// <summary>
    /// Gets the schema version of this configuration document.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Gets general media and source settings.
    /// </summary>
    public GeneralSettings General { get; init; } = new();

    /// <summary>
    /// Gets network provisioning settings used by Foundry.Connect media.
    /// </summary>
    public NetworkSettings Network { get; init; } = new();

    /// <summary>
    /// Gets user-authored OS catalog selection settings used when deployment configuration is generated.
    /// </summary>
    public OperatingSystemSelectionSettings OperatingSystemSelection { get; init; } = new();

    /// <summary>
    /// Gets user-authored localization settings used when deployment configuration is generated.
    /// </summary>
    public LocalizationSettings Localization { get; init; } = new();

    /// <summary>
    /// Gets user-authored customization settings used when deployment configuration is generated.
    /// </summary>
    public CustomizationSettings Customization { get; init; } = new();

    /// <summary>
    /// Gets referenced custom answer files available for protected deployment media.
    /// </summary>
    public UnattendSettings Unattend { get; init; } = new();

    /// <summary>
    /// Gets Autopilot provisioning settings used during deployment.
    /// </summary>
    public AutopilotSettings Autopilot { get; init; } = new();

    /// <summary>
    /// Gets telemetry policy and runtime settings propagated into generated media.
    /// </summary>
    public TelemetrySettings Telemetry { get; init; } = new();
}
