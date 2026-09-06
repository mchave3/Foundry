// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Services.System;
using ComputerNameRules = Foundry.Core.Services.Configuration.ComputerNameRules;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Foundry.Deploy.Services.Hardware;

public sealed class OfflineWindowsComputerNameService : IOfflineWindowsComputerNameService
{
    private const string TempHiveKeyName = "FOUNDRY_OFFLINE_SYSTEM";
    private const string TempHiveKeyPath = @"HKLM\" + TempHiveKeyName;

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<OfflineWindowsComputerNameService> _logger;

    public OfflineWindowsComputerNameService(
        IProcessRunner processRunner,
        ILogger<OfflineWindowsComputerNameService> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<string?> TryGetOfflineComputerNameAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Scanning drive letters for an existing Windows installation.");

        foreach (char drive in GetCandidateDriveLetters())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string hivePath = $@"{drive}:\Windows\System32\config\SYSTEM";
            if (!File.Exists(hivePath))
            {
                continue;
            }

            _logger.LogInformation("Found potential Windows installation at {Drive}:\\. Attempting to read computer name.", drive);

            string? name = await TryReadComputerNameFromHiveAsync(hivePath, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(name))
            {
                _logger.LogInformation(
                    "Resolved offline computer name from {Drive}:\\. ComputerName={ComputerName}",
                    drive,
                    name);
                return name;
            }
        }

        _logger.LogInformation("No offline Windows installation computer name found.");
        return null;
    }

    private async Task<string?> TryReadComputerNameFromHiveAsync(string hivePath, CancellationToken cancellationToken)
    {
        bool hiveLoaded = false;

        try
        {
            ProcessExecutionResult loadResult = await _processRunner
                .RunAsync("reg.exe", ["load", TempHiveKeyPath, hivePath], Path.GetTempPath(), cancellationToken, TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            if (!loadResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Failed to load offline hive at {HivePath}. ExitCode={ExitCode}",
                    hivePath,
                    loadResult.ExitCode);
                return null;
            }

            hiveLoaded = true;

            string controlSetName = ResolveCurrentControlSetName();
            string subKeyPath = $@"{TempHiveKeyName}\{controlSetName}\Control\ComputerName\ComputerName";

            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKeyPath);
            if (key is null)
            {
                _logger.LogDebug("Computer name registry key not found at {SubKeyPath}.", subKeyPath);
                return null;
            }

            string? rawName = key.GetValue("ComputerName") as string;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return null;
            }

            string normalized = ComputerNameRules.Normalize(rawName);
            return ComputerNameRules.IsValid(normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error reading computer name from offline hive at {HivePath}.", hivePath);
            return null;
        }
        finally
        {
            if (hiveLoaded)
            {
                await TryUnloadHiveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads the current control set number from the loaded offline hive's Select key,
    /// falling back to ControlSet001 if it cannot be determined.
    /// </summary>
    private static string ResolveCurrentControlSetName()
    {
        try
        {
            using RegistryKey? selectKey = Registry.LocalMachine.OpenSubKey($@"{TempHiveKeyName}\Select");
            if (selectKey?.GetValue("Current") is int currentSet && currentSet > 0)
            {
                return $"ControlSet{currentSet:D3}";
            }
        }
        catch
        {
            // Fall through to default.
        }

        return "ControlSet001";
    }

    private async Task TryUnloadHiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Give the GC a chance to release any open handles on the hive before unloading.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            ProcessExecutionResult unloadResult = await _processRunner
                .RunAsync("reg.exe", ["unload", TempHiveKeyPath], Path.GetTempPath(), cancellationToken, TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            if (!unloadResult.IsSuccess)
            {
                _logger.LogWarning("Failed to unload offline hive. ExitCode={ExitCode}", unloadResult.ExitCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error while unloading offline hive.");
        }
    }

    /// <summary>
    /// Returns candidate drive letters to scan, excluding the current system drive (WinPE's X:)
    /// and the legacy floppy drives (A:, B:).
    /// </summary>
    private static IEnumerable<char> GetCandidateDriveLetters()
    {
        string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "X:";
        char systemLetter = char.ToUpperInvariant(systemDrive.Length > 0 ? systemDrive[0] : 'X');

        return Enumerable
            .Range('C', 'Z' - 'C' + 1)
            .Select(c => (char)c)
            .Where(c => c != systemLetter);
    }
}
