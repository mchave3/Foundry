// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.DriverPacks;

/// <summary>Selects an OEM pack only when catalog compatibility matches the discovered machine.</summary>
public sealed class DriverPackSelectionService : IDriverPackSelectionService
{
    private readonly ILogger<DriverPackSelectionService> _logger;

    public DriverPackSelectionService(ILogger<DriverPackSelectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>Returns the newest exact compatible match without inferring model or release compatibility.</summary>
    public DriverPackSelectionResult SelectBest(
        IReadOnlyList<DriverPackCatalogItem> catalog,
        HardwareProfile hardware,
        OperatingSystemCatalogItem operatingSystem)
    {
        _logger.LogDebug("Selecting compatible OEM driver pack. CatalogCount={CatalogCount}, WindowsRelease={WindowsRelease}, ReleaseId={ReleaseId}, OsArchitecture={OsArchitecture}",
            catalog.Count,
            operatingSystem.WindowsRelease,
            operatingSystem.ReleaseId,
            operatingSystem.Architecture);

        if (catalog.Count == 0)
        {
            return new DriverPackSelectionResult
            {
                DriverPack = null,
                SelectionReason = "Driver catalog is empty."
            };
        }

        if (!OperatingSystemSupportMatrix.IsSupported(operatingSystem))
        {
            return new DriverPackSelectionResult
            {
                DriverPack = null,
                SelectionReason =
                    $"Unsupported operating system selection. Foundry.Deploy supports Windows {OperatingSystemSupportMatrix.SupportedWindowsRelease} 23H2, 24H2, and 25H2 only."
            };
        }

        string osArch = NormalizeArchitecture(operatingSystem.Architecture);
        string manufacturer = NormalizeManufacturer(hardware.Manufacturer);
        if (string.IsNullOrEmpty(manufacturer) || string.IsNullOrEmpty(osArch) ||
            NormalizeArchitecture(hardware.Architecture) != osArch)
        {
            return NoCompatiblePack();
        }

        DriverPackCatalogItem? exactModel = catalog
            .Where(item => NormalizeManufacturer(item.Manufacturer) == manufacturer)
            .Where(item => NormalizeArchitecture(item.OsArchitecture) == osArch)
            .Where(item => Normalize(item.OsName) == "windows 11")
            .Where(item => Normalize(item.OsReleaseId) == Normalize(operatingSystem.ReleaseId))
            .Where(item => IsHardwareMatch(item, hardware, manufacturer))
            .OrderByDescending(item => item.ReleaseDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (exactModel is not null)
        {
            _logger.LogDebug("OEM driver pack selected by compatible hardware match.");
            return new DriverPackSelectionResult
            {
                DriverPack = exactModel,
                SelectionReason = "Matched by hardware model/product and compatible OS release."
            };
        }

        return NoCompatiblePack();
    }

    private DriverPackSelectionResult NoCompatiblePack()
    {
        _logger.LogDebug("No compatible OEM driver pack matched the discovered hardware and selected OS.");
        return new DriverPackSelectionResult
        {
            SelectionReason = "No compatible OEM system pack matches the detected model and exact OS release. Select a pack explicitly or use Microsoft Update Catalog."
        };
    }

    private static bool IsHardwareMatch(DriverPackCatalogItem item, HardwareProfile hardware, string manufacturer)
    {
        if (item.PackageRole == DriverPackPackageRole.Accessory)
        {
            return false;
        }

        if (manufacturer == "lenovo")
        {
            string[] machineTypes = new[] { hardware.Model, hardware.Product }
                .Select(GetLenovoMachineType)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (machineTypes.Length > 1)
            {
                return false;
            }

            if (machineTypes.Length == 1 && item.SystemIds.Count > 0)
            {
                return item.SystemIds.Any(systemId => Normalize(systemId) == machineTypes[0]);
            }
        }

        string model = Normalize(hardware.Model);
        string product = Normalize(hardware.Product);
        return item.PackageRole == DriverPackPackageRole.System && item.ModelNames.Any(modelName =>
            IsKnownModelMatch(Normalize(modelName), model) || IsKnownModelMatch(Normalize(modelName), product));
    }

    private static bool IsKnownModelMatch(string catalogModel, string detectedModel)
    {
        return detectedModel.Length > 0 && detectedModel != "unknown" && catalogModel == detectedModel;
    }

    private static string GetLenovoMachineType(string value)
    {
        string normalized = Normalize(value);
        // Lenovo defines the machine type as four characters, or the first four of a ten-character MTM.
        if (normalized.Length is not (4 or 10) || !char.IsAsciiDigit(normalized[0]) ||
            !normalized.All(char.IsAsciiLetterOrDigit))
        {
            return string.Empty;
        }

        return normalized[..4];
    }

    private static string Normalize(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static string NormalizeManufacturer(string value)
    {
        return Normalize(value) switch
        {
            "hp" or "hp inc." or "hewlett-packard" or "hewlett packard" => "hp",
            "dell" or "dell inc." or "dell inc" => "dell",
            "lenovo" => "lenovo",
            "microsoft" or "microsoft corporation" => "microsoft",
            _ => string.Empty
        };
    }

    private static string NormalizeArchitecture(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "amd64" => "x64",
            "x64" => "x64",
            "arm64" => "arm64",
            "aarch64" => "arm64",
            _ => string.Empty
        };
    }
}
