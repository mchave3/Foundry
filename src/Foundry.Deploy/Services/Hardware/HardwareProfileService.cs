// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Models;
using Foundry.Utilities.Hardware;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Hardware;

public sealed class HardwareProfileService : IHardwareProfileService
{
    private readonly IHardwareInspector _hardwareInspector;
    private readonly ILogger<HardwareProfileService> _logger;

    public HardwareProfileService(
        IHardwareInspector hardwareInspector,
        ILogger<HardwareProfileService> logger)
    {
        _hardwareInspector = hardwareInspector;
        _logger = logger;
    }

    public async Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Detecting current hardware profile.");
        try
        {
            HardwareSnapshot snapshot = await _hardwareInspector
                .GetCurrentAsync(cancellationToken)
                .ConfigureAwait(false);

            HardwareProfile profile = new()
            {
                FirmwareType = snapshot.FirmwareType,
                Manufacturer = NormalizeManufacturer(snapshot.Manufacturer),
                Model = NormalizeValue(snapshot.Model),
                Product = NormalizeValue(snapshot.Product),
                SerialNumber = NormalizeValue(snapshot.SerialNumber),
                AssetTag = NormalizeValue(snapshot.AssetTag),
                SystemUuid = NormalizeValue(snapshot.SystemUuid),
                Architecture = snapshot.Architecture,
                IsVirtualMachine = snapshot.IsVirtualMachine,
                IsOnBattery = snapshot.IsOnBattery,
                IsTpmPresent = snapshot.IsTpmPresent,
                SystemFirmwareHardwareId = snapshot.SystemFirmwareHardwareId.Trim(),
                PnpDevices = snapshot.PnpDevices.Select(MapPnpDevice).ToArray()
            };

            _logger.LogInformation("Hardware profile detected. Manufacturer={Manufacturer}, Model={Model}, Architecture={Architecture}, IsVirtualMachine={IsVirtualMachine}, IsOnBattery={IsOnBattery}, IsTpmPresent={IsTpmPresent}",
                profile.Manufacturer,
                profile.Model,
                profile.Architecture,
                profile.IsVirtualMachine,
                profile.IsOnBattery,
                profile.IsTpmPresent);
            return profile;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Hardware profile detection returned no data. Using fallback profile.");
            return BuildFallbackProfile();
        }
    }

    private static PnpDeviceInfo MapPnpDevice(PnpDeviceSnapshot device)
    {
        return new PnpDeviceInfo
        {
            Name = device.Name,
            DeviceId = device.DeviceId,
            HardwareIds = device.HardwareIds,
            ClassGuid = device.ClassGuid,
            Manufacturer = device.Manufacturer,
            PnpClass = device.PnpClass
        };
    }

    private static HardwareProfile BuildFallbackProfile()
    {
        string architecture = NormalizeArchitecture(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty);
        return new HardwareProfile
        {
            Manufacturer = "Unknown",
            Model = "Unknown",
            Product = "Unknown",
            SerialNumber = "Unknown",
            AssetTag = "Unknown",
            SystemUuid = "Unknown",
            Architecture = architecture,
            IsVirtualMachine = false,
            IsOnBattery = false,
            IsTpmPresent = false,
            SystemFirmwareHardwareId = string.Empty,
            PnpDevices = Array.Empty<PnpDeviceInfo>()
        };
    }

    private static string NormalizeManufacturer(string value)
    {
        string normalized = NormalizeValue(value);
        if (normalized.Contains("Hewlett", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("HP", StringComparison.OrdinalIgnoreCase))
        {
            return "HP";
        }

        if (normalized.Contains("Dell", StringComparison.OrdinalIgnoreCase))
        {
            return "Dell";
        }

        if (normalized.Contains("Lenovo", StringComparison.OrdinalIgnoreCase))
        {
            return "Lenovo";
        }

        if (normalized.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft";
        }

        return normalized;
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
            _ => normalized
        };
    }

    private static string NormalizeValue(string value)
    {
        string normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "Unknown" : normalized;
    }
}
