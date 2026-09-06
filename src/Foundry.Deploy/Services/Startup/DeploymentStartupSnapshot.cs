// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Catalog;
using Foundry.Deploy.Services.System;

namespace Foundry.Deploy.Services.Startup;

public sealed record DeploymentStartupSnapshot
{
    /// <summary>Locates the protected answer-file catalog relative to the loaded runtime configuration.</summary>
    public string ConfigurationPath { get; init; } = Services.Configuration.DeployConfigurationService.DefaultConfigurationPath;
    /// <summary>Preserves a configuration failure instead of falling back to native customization.</summary>
    public string? ConfigurationFailureMessage { get; init; }

    public required string CacheRootPath { get; init; }
    public required FoundryDeployConfigurationDocument? DeployConfigurationDocument { get; init; }
    public required bool IsBootMediaUpdateRecommended { get; init; }
    public required IReadOnlyList<AutopilotProfileCatalogItem> AutopilotProfiles { get; init; }
    public required MachineNamePreparationResult MachineNamePreparation { get; init; }
    public required HardwareProfile? DetectedHardware { get; init; }
    public required string? HardwareDetectionFailureMessage { get; init; }
    public required IReadOnlyList<TargetDiskInfo> TargetDisks { get; init; }
    public required DeploymentCatalogSnapshot CatalogSnapshot { get; init; }
}
