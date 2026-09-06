// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;

namespace Foundry.Deploy.Services.DriverPacks;

public interface IMicrosoftUpdateCatalogFirmwareService
{
    Task<MicrosoftUpdateCatalogFirmwareResult> DownloadAsync(
        HardwareProfile hardwareProfile,
        OperatingSystemCatalogItem operatingSystem,
        string rawDirectory,
        string extractedDirectory,
        string cacheDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null);
}
