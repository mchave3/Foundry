// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration.Deploy;

namespace Foundry.Deploy.Models;

/// <summary>Identifies one protected answer file packaged beside the runtime configuration.</summary>
public sealed record UnattendSelection(DeployUnattendFile File, string AssetPath);
