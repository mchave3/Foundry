// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using CoreDeployNetworkSettings = Foundry.Core.Models.Configuration.Deploy.DeployNetworkSettings;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>
/// Contains the immutable deployment request selected by the wizard.
/// </summary>
public sealed record DeploymentContext
{
    /// <summary>Gets the selected custom file; null retains native Foundry customization.</summary>
    public UnattendSelection? Unattend { get; init; }

    /// <summary>Gets whether a custom answer file replaces the Foundry-generated answer file without merging settings.</summary>
    public bool UsesCustomUnattend => Unattend is not null;

    /// <summary>
    /// Gets the deployment source mode.
    /// </summary>
    public required DeploymentMode Mode { get; init; }

    /// <summary>
    /// Gets the requested cache root path.
    /// </summary>
    public required string CacheRootPath { get; init; }

    /// <summary>
    /// Gets the target disk number selected for deployment.
    /// </summary>
    public required int TargetDiskNumber { get; init; }

    /// <summary>
    /// Gets the target computer name written into unattend.xml.
    /// </summary>
    public required string TargetComputerName { get; init; }

    /// <summary>
    /// Gets the optional default Windows time zone ID written into unattend.xml.
    /// </summary>
    public string? DefaultTimeZoneId { get; init; }

    /// <summary>
    /// Gets the operating system catalog item to download and apply.
    /// </summary>
    public required OperatingSystemCatalogItem OperatingSystem { get; init; }

    /// <summary>
    /// Gets the selected driver pack strategy.
    /// </summary>
    public required DriverPackSelectionKind DriverPackSelectionKind { get; init; }

    /// <summary>
    /// Gets the selected catalog driver pack when a catalog-backed strategy is used.
    /// </summary>
    public DriverPackCatalogItem? DriverPack { get; init; }

    /// <summary>
    /// Gets a value indicating whether matching firmware updates should be downloaded and applied.
    /// </summary>
    public bool ApplyFirmwareUpdates { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the selected Autopilot provisioning method should run.
    /// </summary>
    public bool IsAutopilotEnabled { get; init; }

    /// <summary>
    /// Gets the selected Autopilot provisioning mode.
    /// </summary>
    public AutopilotProvisioningMode AutopilotProvisioningMode { get; init; } = AutopilotProvisioningMode.JsonProfile;

    /// <summary>
    /// Gets the selected Autopilot profile staged into the offline Windows image when JSON profile mode is selected.
    /// </summary>
    public AutopilotProfileCatalogItem? SelectedAutopilotProfile { get; init; }

    /// <summary>
    /// Gets non-secret metadata for hardware hash upload mode, including the effective group tag override.
    /// </summary>
    public DeployAutopilotHardwareHashUploadSettings AutopilotHardwareHashUpload { get; init; } = new();

    /// <summary>
    /// Gets network settings used by late deployment steps.
    /// </summary>
    public CoreDeployNetworkSettings Network { get; init; } = new();

    /// <summary>
    /// Gets Windows OOBE customization settings applied to the offline installation.
    /// </summary>
    public DeployOobeSettings Oobe { get; init; } = new();

    /// <summary>
    /// Gets provisioned AppX removal settings staged for pre-OOBE execution.
    /// </summary>
    public DeployAppxRemovalSettings AppxRemoval { get; init; } = new();

    /// <summary>
    /// Gets Windows AI component removal settings staged for pre-OOBE execution.
    /// </summary>
    public DeployAiComponentRemovalSettings AiComponentRemoval { get; init; } = new();

    /// <summary>
    /// Gets Windows optional feature changes applied to the offline installation.
    /// </summary>
    public DeployWindowsOptionalFeatureSettings WindowsOptionalFeatures { get; init; } = new();

    /// <summary>
    /// Gets the completion settings used after deployment finishes.
    /// </summary>
    public DeployCompletionSettings Completion { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether deployment runs against a temporary workspace instead of mutating a target disk.
    /// </summary>
    public bool IsDryRun { get; init; }
}
