// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.IO;
using System.Text.Json;
using Foundry.Utilities.Processes;
using Foundry.Utilities.Serialization;

namespace Foundry.Utilities.Storage;

/// <summary>
/// Inspects Windows disks through PowerShell storage cmdlets.
/// </summary>
public sealed class WindowsDiskInspector : IWindowsDiskInspector
{
    private const string DiskQueryScript = """
        $disks = Get-Disk | Sort-Object -Property Number
        $result = foreach ($disk in $disks) {
            [pscustomobject]@{
                Number = [int]$disk.Number
                FriendlyName = [string]$disk.FriendlyName
                UniqueId = [string]$disk.UniqueId
                SerialNumber = [string]$disk.SerialNumber
                BusType = [string]$disk.BusType
                PartitionStyle = [string]$disk.PartitionStyle
                Size = [uint64]$disk.Size
                IsSystem = [bool]$disk.IsSystem
                IsBoot = [bool]$disk.IsBoot
                IsReadOnly = [bool]$disk.IsReadOnly
                IsOffline = [bool]$disk.IsOffline
                IsRemovable = [bool]$disk.IsRemovable
            }
        }
        $result | ConvertTo-Json -Compress
        """;

    private readonly Func<ProcessExecutionRequest, CancellationToken, Task<ProcessExecutionResult>> _executeProcess;

    /// <summary>
    /// Initializes a new inspector using the shared process runner.
    /// </summary>
    public WindowsDiskInspector(ProcessRunner processRunner)
        : this(processRunner.RunAsync)
    {
    }

    internal WindowsDiskInspector(
        Func<ProcessExecutionRequest, CancellationToken, Task<ProcessExecutionResult>> executeProcess)
    {
        ArgumentNullException.ThrowIfNull(executeProcess);
        _executeProcess = executeProcess;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiskInfo>> GetDisksAsync(
        CancellationToken cancellationToken = default)
    {
        ProcessExecutionResult execution = await ExecuteScriptAsync(DiskQueryScript, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessfulOutput(execution, "Disk query");

        try
        {
            return JsonObjectSequence.Parse(execution.StandardOutput)
                .Select(ParseDisk)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Failed to parse the disk query payload.", exception);
        }
    }

    /// <inheritdoc />
    public async Task<int?> ResolveDiskNumberForPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root) ||
            !Path.IsPathFullyQualified(path) ||
            root.Length < 2 ||
            root[1] != ':' ||
            !char.IsAsciiLetter(root[0]) ||
            root.AsSpan(2).ContainsAnyExcept('\\', '/'))
        {
            return null;
        }

        string driveLetter = char.ToUpperInvariant(root[0]).ToString(CultureInfo.InvariantCulture);

        string script = $@"
$partition = Get-Partition -DriveLetter '{EscapeForSingleQuote(driveLetter)}' -ErrorAction SilentlyContinue
if ($null -eq $partition) {{
    return
}}

[pscustomobject]@{{
    DiskNumber = [int]$partition.DiskNumber
}} | ConvertTo-Json -Compress
";

        ProcessExecutionResult execution = await ExecuteScriptAsync(script, cancellationToken).ConfigureAwait(false);
        if (!execution.IsSuccess)
        {
            throw new InvalidDataException(
                $"Disk number lookup failed. ExitCode={execution.ExitCode}.");
        }

        if (string.IsNullOrWhiteSpace(execution.StandardOutput))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(execution.StandardOutput);
            JsonElement rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object ||
                !rootElement.TryGetProperty("DiskNumber", out JsonElement diskNumberElement))
            {
                return null;
            }

            int? diskNumber = ReadNullableInt(diskNumberElement);
            return diskNumber >= 0 ? diskNumber : null;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Failed to parse the disk number lookup payload.", exception);
        }
    }

    private async Task<ProcessExecutionResult> ExecuteScriptAsync(
        string script,
        CancellationToken cancellationToken)
    {
        var request = new ProcessExecutionRequest(
            "powershell.exe",
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                .. PowerShellCommand.CreateEncodedArguments(script)
            ],
            Path.GetTempPath())
        {
            ExecutionTimeout = TimeSpan.FromMinutes(1)
        };

        ProcessExecutionResult execution = await _executeProcess(request, cancellationToken).ConfigureAwait(false);
        execution.EnsureCompleteOutput();
        return execution;
    }

    private static DiskInfo ParseDisk(JsonElement element)
    {
        return new DiskInfo(
            ReadRequiredNonNegativeInt(element, "Number"),
            ReadString(element, "FriendlyName"),
            ReadString(element, "SerialNumber"),
            ReadString(element, "BusType"),
            ReadString(element, "PartitionStyle"),
            ReadUInt64(element, "Size"),
            ReadRequiredBool(element, "IsSystem"),
            ReadRequiredBool(element, "IsBoot"),
            ReadRequiredBool(element, "IsReadOnly"),
            ReadRequiredBool(element, "IsOffline"),
            ReadBool(element, "IsRemovable"))
        { UniqueId = ReadString(element, "UniqueId") };
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : property.ToString().Trim();
    }

    private static int ReadRequiredNonNegativeInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property) ||
            ReadNullableInt(property) is not int value ||
            value < 0)
        {
            throw new JsonException($"Disk property '{propertyName}' must be a non-negative integer.");
        }

        return value;
    }

    private static int? ReadNullableInt(JsonElement property)
    {
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static ulong ReadUInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out ulong value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               ulong.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)
            ? parsed
            : 0;
    }

    private static bool ReadBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.True ||
               (property.ValueKind == JsonValueKind.String &&
                bool.TryParse(property.GetString(), out bool parsed) &&
                parsed);
    }

    private static bool ReadRequiredBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property))
        {
            throw new JsonException($"Disk property '{propertyName}' is required.");
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String &&
            bool.TryParse(property.GetString(), out bool parsed))
        {
            return parsed;
        }

        throw new JsonException($"Disk property '{propertyName}' must be a Boolean.");
    }

    private static void EnsureSuccessfulOutput(ProcessExecutionResult execution, string operation)
    {
        if (!execution.IsSuccess || string.IsNullOrWhiteSpace(execution.StandardOutput))
        {
            throw new InvalidDataException($"{operation} returned no data. ExitCode={execution.ExitCode}.");
        }
    }

    private static string EscapeForSingleQuote(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
