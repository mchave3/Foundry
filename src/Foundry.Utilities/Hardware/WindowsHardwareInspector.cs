// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text.Json;
using Foundry.Utilities.Processes;
using Foundry.Utilities.Serialization;

namespace Foundry.Utilities.Hardware;

/// <summary>
/// Inspects Windows hardware through CIM and PowerShell.
/// </summary>
public sealed class WindowsHardwareInspector : IHardwareInspector
{
    private const string InspectionScript = """
        function ConvertTo-TrimmedString {
            param (
                [Parameter(ValueFromPipeline = $true)]
                $Value
            )

            process {
                if ($null -eq $Value) {
                    return ''
                }

                return $Value.ToString().Trim()
            }
        }

        $computer = Get-CimInstance -ClassName Win32_ComputerSystem
        $product = Get-CimInstance -ClassName Win32_ComputerSystemProduct
        $bios = Get-CimInstance -ClassName Win32_BIOS
        $enclosure = Get-CimInstance -ClassName Win32_SystemEnclosure
        $tpm = Get-CimInstance -Namespace 'ROOT\cimv2\Security\MicrosoftTpm' -ClassName Win32_Tpm -ErrorAction SilentlyContinue
        $battery = Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue
        $pnpDevices = @(Get-CimInstance -ClassName Win32_PnpEntity -Property Name,DeviceID,HardwareID,ClassGuid,Manufacturer,PNPClass -ErrorAction SilentlyContinue | ForEach-Object {
            $hardwareIds = @($_.HardwareID | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.ToString().Trim() })
            [pscustomobject]@{
                Name = [string]($_.Name | ConvertTo-TrimmedString)
                DeviceId = [string]($_.DeviceID | ConvertTo-TrimmedString)
                HardwareIds = $hardwareIds
                ClassGuid = [string]($_.ClassGuid | ConvertTo-TrimmedString)
                Manufacturer = [string]($_.Manufacturer | ConvertTo-TrimmedString)
                PnpClass = [string]($_.PNPClass | ConvertTo-TrimmedString)
            }
        })
        $firmwareDevice = $pnpDevices | Where-Object { $_.ClassGuid -eq '{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}' } | Select-Object -First 1
        $systemFirmwareHardwareId = ''
        if ($firmwareDevice -and $firmwareDevice.DeviceId -match '\{?(([0-9a-f]){8}-([0-9a-f]){4}-([0-9a-f]){4}-([0-9a-f]){4}-([0-9a-f]){12})\}?') {
            $systemFirmwareHardwareId = $Matches[1]
        }
        $isOnBattery = @($battery | Where-Object { $_.BatteryStatus -eq 1 }).Count -gt 0

        [pscustomobject]@{
            Manufacturer = [string]$computer.Manufacturer
            Model = [string]$computer.Model
            Product = [string]$product.Version
            SerialNumber = [string]$bios.SerialNumber
            AssetTag = [string]$enclosure.SMBIOSAssetTag
            SystemUuid = [string]$product.UUID
            Architecture = [string]$env:PROCESSOR_ARCHITECTURE
            IsOnBattery = [bool]$isOnBattery
            IsTpmPresent = [bool]($null -ne $tpm)
            SystemFirmwareHardwareId = [string]$systemFirmwareHardwareId
            PnpDevices = $pnpDevices
        } | ConvertTo-Json -Compress -Depth 8
        """;

    private readonly Func<WindowsFirmwareType> _readFirmware;
    private readonly Func<ProcessExecutionRequest, CancellationToken, Task<ProcessExecutionResult>> _executeProcess;

    /// <summary>
    /// Initializes a new inspector using the shared process runner.
    /// </summary>
    public WindowsHardwareInspector(ProcessRunner processRunner)
        : this(processRunner.RunAsync)
    {
    }

    internal WindowsHardwareInspector(
        Func<ProcessExecutionRequest, CancellationToken, Task<ProcessExecutionResult>> executeProcess,
        Func<WindowsFirmwareType>? readFirmware = null)
    {
        ArgumentNullException.ThrowIfNull(executeProcess);
        _executeProcess = executeProcess;
        _readFirmware = readFirmware ?? WindowsFirmwareInspector.GetCurrent;
    }

    /// <inheritdoc />
    public async Task<HardwareSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var request = new ProcessExecutionRequest(
            "powershell.exe",
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                .. PowerShellCommand.CreateEncodedArguments(InspectionScript)
            ],
            Path.GetTempPath())
        {
            ExecutionTimeout = TimeSpan.FromMinutes(1)
        };

        ProcessExecutionResult execution = await _executeProcess(request, cancellationToken).ConfigureAwait(false);
        execution.EnsureCompleteOutput();
        if (!execution.IsSuccess || string.IsNullOrWhiteSpace(execution.StandardOutput))
        {
            throw new InvalidDataException(
                $"Hardware inspection returned no data. ExitCode={execution.ExitCode}.");
        }

        try
        {
            IReadOnlyList<JsonElement> roots = JsonObjectSequence.Parse(execution.StandardOutput);
            if (roots.Count != 1)
            {
                throw new JsonException("Hardware inspection must return exactly one object.");
            }

            return ParseSnapshot(roots[0]) with { FirmwareType = _readFirmware() };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Failed to parse the hardware inspection payload.", exception);
        }
    }

    private static HardwareSnapshot ParseSnapshot(JsonElement root)
    {
        string manufacturer = ReadString(root, "Manufacturer");
        string model = ReadString(root, "Model");
        string product = ReadString(root, "Product");

        return new HardwareSnapshot(
            manufacturer,
            model,
            product,
            ReadString(root, "SerialNumber"),
            NormalizeArchitecture(ReadString(root, "Architecture")),
            IsVirtualMachine(manufacturer, model, product),
            ReadBool(root, "IsOnBattery"),
            ReadBool(root, "IsTpmPresent"),
            ReadString(root, "SystemFirmwareHardwareId"),
            ReadPnpDevices(root))
        {
            AssetTag = ReadString(root, "AssetTag"),
            SystemUuid = ReadString(root, "SystemUuid")
        };
    }

    private static IReadOnlyList<PnpDeviceSnapshot> ReadPnpDevices(JsonElement root)
    {
        if (!root.TryGetProperty("PnpDevices", out JsonElement devicesElement))
        {
            return [];
        }

        if (devicesElement.ValueKind == JsonValueKind.Object)
        {
            return [ParsePnpDevice(devicesElement)];
        }

        if (devicesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return devicesElement
            .EnumerateArray()
            .Where(static element => element.ValueKind == JsonValueKind.Object)
            .Select(ParsePnpDevice)
            .ToArray();
    }

    private static PnpDeviceSnapshot ParsePnpDevice(JsonElement element)
    {
        return new PnpDeviceSnapshot(
            ReadString(element, "Name"),
            ReadString(element, "DeviceId"),
            ReadStringArray(element, "HardwareIds"),
            ReadString(element, "ClassGuid"),
            ReadString(element, "Manufacturer"),
            ReadString(element, "PnpClass"));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string item = value.GetString()?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(item) ? [] : [item];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : value.ToString().Trim();
    }

    private static bool ReadBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out bool parsed) &&
                parsed);
    }

    private static string NormalizeArchitecture(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "amd64" or "x64" => "x64",
            "arm64" or "aarch64" => "arm64",
            _ => normalized
        };
    }

    private static bool IsVirtualMachine(string manufacturer, string model, string product)
    {
        string combined = string.Join(" | ", manufacturer, model, product).ToLowerInvariant();
        if (combined.Contains("vmware") ||
            combined.Contains("virtualbox") ||
            combined.Contains("virtual machine") ||
            combined.Contains("kvm") ||
            combined.Contains("qemu") ||
            combined.Contains("xen") ||
            combined.Contains("hvm domu") ||
            combined.Contains("parallels") ||
            combined.Contains("bhyve"))
        {
            return true;
        }

        return combined.Contains("microsoft corporation") && combined.Contains("virtual");
    }
}
