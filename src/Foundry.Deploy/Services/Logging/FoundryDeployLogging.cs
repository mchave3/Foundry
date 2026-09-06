// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Telemetry;
using Foundry.Utilities.IO;
using Foundry.Utilities.Diagnostics;
using Serilog;
using Serilog.Events;

namespace Foundry.Deploy.Services.Logging;

internal static class FoundryDeployLogging
{
    public const string LogFileName = "FoundryDeploy.log";

    private const int RetainedLogFileCount = 5;
    private static readonly object PersistenceSync = new();
    private static string? _startupLogDirectoryPath;
    private static string? _persistenceDirectoryPath;

    public static string CurrentLogFilePath { get; private set; } = "<unavailable>";

    public static string ResolveStartupLogFilePath()
    {
        string[] candidateDirectories =
        [
            @"X:\Foundry\Logs",
            Path.Combine(Path.GetTempPath(), "Foundry", "Logs"),
            AppContext.BaseDirectory
        ];

        return WritableFilePathResolver.Resolve(candidateDirectories, LogFileName);
    }

    public static ILogger CreateLogger(string logFilePath)
    {
        string normalizedLogFilePath = Path.GetFullPath(logFilePath);
        ILogger logger = FoundryLogConfiguration.CreateFileLogger(
            logFilePath,
            "Foundry.Deploy",
            DiagnosticSessionContext.CurrentSessionId,
            LogEventLevel.Debug,
            RetainedLogFileCount,
            additionalSink: RemoteDiagnosticsSink.Instance);

        CurrentLogFilePath = normalizedLogFilePath;
        _startupLogDirectoryPath = Path.GetDirectoryName(normalizedLogFilePath);
        return VolumePathDiagnostics.WrapLogger(logger);
    }

    public static void RegisterPersistenceDirectory(string logsDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectoryPath);
        _persistenceDirectoryPath = Path.GetFullPath(logsDirectoryPath);
    }

    public static LogPersistenceResult PersistCurrentLogs()
    {
        string? sourceDirectoryPath = _startupLogDirectoryPath;
        if (string.IsNullOrWhiteSpace(sourceDirectoryPath))
        {
            return new LogPersistenceResult(0, 0);
        }

        string[] targetDirectoryPaths =
        [
            .. new[]
            {
                _persistenceDirectoryPath,
                Environment.GetEnvironmentVariable(DiagnosticSessionContext.PersistenceDirectoryEnvironmentVariableName)
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        int copiedFileCount = 0;
        int failedFileCount = 0;
        foreach (string targetDirectoryPath in targetDirectoryPaths)
        {
            LogPersistenceResult result = PersistLogSnapshot(sourceDirectoryPath, targetDirectoryPath);
            copiedFileCount += result.CopiedFileCount;
            failedFileCount += result.FailedFileCount;
        }

        return new LogPersistenceResult(copiedFileCount, failedFileCount);
    }

    internal static LogPersistenceResult PersistLogSnapshot(
        string sourceDirectoryPath,
        string targetDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectoryPath);

        string normalizedSource = Path.GetFullPath(sourceDirectoryPath);
        string normalizedTarget = Path.GetFullPath(targetDirectoryPath);
        if (normalizedSource.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(normalizedSource))
        {
            return new LogPersistenceResult(0, 0);
        }

        lock (PersistenceSync)
        {
            int copiedFileCount = 0;
            int failedFileCount = 0;
            try
            {
                Directory.CreateDirectory(normalizedTarget);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                global::System.Diagnostics.Debug.WriteLine(
                    $"Foundry.Deploy log persistence directory is unavailable: {ex.GetType().Name}");
                return new LogPersistenceResult(0, 1);
            }

            string[] sourceFilePaths;
            try
            {
                sourceFilePaths = Directory.GetFiles(
                    normalizedSource,
                    "*.log",
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                global::System.Diagnostics.Debug.WriteLine(
                    $"Foundry.Deploy log source enumeration failed: {ex.GetType().Name}");
                return new LogPersistenceResult(0, 1);
            }

            foreach (string sourceFilePath in sourceFilePaths)
            {
                string destinationPath = Path.Combine(normalizedTarget, Path.GetFileName(sourceFilePath));
                string temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    using (FileStream source = new(
                        sourceFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (FileStream destination = new(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        source.CopyTo(destination);
                    }

                    File.Move(temporaryPath, destinationPath, overwrite: true);
                    copiedFileCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    failedFileCount++;
                    global::System.Diagnostics.Debug.WriteLine(
                        $"Foundry.Deploy log file persistence failed: {ex.GetType().Name}");
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        try
                        {
                            File.Delete(temporaryPath);
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }
            }

            return new LogPersistenceResult(copiedFileCount, failedFileCount);
        }
    }
}

