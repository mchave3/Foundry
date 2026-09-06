// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;

namespace Foundry.Core.Services.WinPe;

public sealed record WinPeMountedImageAssetProvisioningOptions
{
    public string MountedImagePath { get; init; } = string.Empty;
    public WinPeArchitecture Architecture { get; init; } = WinPeArchitecture.X64;
    public string BootstrapScriptContent { get; init; } = string.Empty;
    public string CurlExecutableSourcePath { get; init; } = string.Empty;
    public string SevenZipSourceDirectoryPath { get; init; } = string.Empty;
    public string IanaWindowsTimeZoneMapJson { get; init; } = string.Empty;
    public string FoundryConnectConfigurationJson { get; init; } = string.Empty;
    public string DeployConfigurationJson { get; init; } = string.Empty;
    public byte[]? NetworkSecretsKey { get; init; }
    public byte[]? DeploymentSecretsKey { get; init; }
    public bool IsDeploymentProtectionEnabled { get; init; }

    /// <summary>
    /// Gets source references validated and encrypted when custom answer files are embedded in media.
    /// </summary>
    public UnattendSettings Unattend { get; init; } = new();
    public IReadOnlyList<FoundryConnectProvisionedAssetFile> FoundryConnectAssetFiles { get; init; } = [];

    /// <summary>
    /// Gets the Autopilot mode that controls whether profile JSON or hash capture assets are staged.
    /// </summary>
    public AutopilotProvisioningMode AutopilotProvisioningMode { get; init; } = AutopilotProvisioningMode.JsonProfile;

    /// <summary>
    /// Gets the ADK OA3Tool source path copied into media when hardware hash upload mode is selected.
    /// </summary>
    public string? Oa3ToolSourcePath { get; init; }

    public IReadOnlyList<AutopilotProfileSettings> AutopilotProfiles { get; init; } = [];
    public WinPeProvisioningSource ConnectProvisioningSource { get; init; } = WinPeProvisioningSource.Release;
    public WinPeProvisioningSource DeployProvisioningSource { get; init; } = WinPeProvisioningSource.Release;
}
