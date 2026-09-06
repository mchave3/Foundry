// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using CoreDeployNetworkSettings = Foundry.Core.Models.Configuration.Deploy.DeployNetworkSettings;

namespace Foundry.Deploy.Services.Deployment;

public sealed record DeploymentLaunchRequest
{
    /// <summary>Gets the selected custom file; null retains native Foundry customization.</summary>
    public UnattendSelection? Unattend { get; init; }

    /// <summary>Gets whether a custom answer file replaces the Foundry-generated answer file without merging settings.</summary>
    public bool UsesCustomUnattend => Unattend is not null;

    public required DeploymentMode Mode { get; init; }
    public required string CacheRootPath { get; init; }
    public required string TargetComputerName { get; init; }
    public string? DefaultTimeZoneId { get; init; }
    public required TargetDiskInfo? SelectedTargetDisk { get; init; }
    public required OperatingSystemCatalogItem? SelectedOperatingSystem { get; init; }
    public required DriverPackSelectionKind DriverPackSelectionKind { get; init; }
    public required DriverPackCatalogItem? SelectedDriverPack { get; init; }
    public required bool ApplyFirmwareUpdates { get; init; }
    public required bool IsAutopilotEnabled { get; init; }
    public required AutopilotProvisioningMode AutopilotProvisioningMode { get; init; }
    public required AutopilotProfileCatalogItem? SelectedAutopilotProfile { get; init; }
    public DeployAutopilotHardwareHashUploadSettings AutopilotHardwareHashUpload { get; init; } = new();
    public CoreDeployNetworkSettings Network { get; init; } = new();
    public DeployOobeSettings Oobe { get; init; } = new();
    public DeployAppxRemovalSettings AppxRemoval { get; init; } = new();
    public DeployAiComponentRemovalSettings AiComponentRemoval { get; init; } = new();
    public DeployWindowsOptionalFeatureSettings WindowsOptionalFeatures { get; init; } = new();
    public DeployCompletionSettings Completion { get; init; } = new();
    public required bool IsDryRun { get; init; }
}
