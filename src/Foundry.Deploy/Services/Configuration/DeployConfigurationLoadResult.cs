// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models.Configuration;

namespace Foundry.Deploy.Services.Configuration;

public sealed record DeployConfigurationLoadResult
{
    public string ConfigurationPath { get; init; } = DeployConfigurationService.DefaultConfigurationPath;
    public bool Exists { get; init; }
    public FoundryDeployConfigurationDocument? Document { get; init; }
    public bool IsBootMediaUpdateRecommended { get; init; }
    public bool IsUnsupportedSchemaVersion { get; init; }
    public string? FailureMessage { get; init; }
}
