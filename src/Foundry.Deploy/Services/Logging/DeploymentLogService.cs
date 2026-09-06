// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text.Json;
using Foundry.Utilities.Diagnostics;
using Serilog;
using Serilog.Events;

namespace Foundry.Deploy.Services.Logging;

public sealed class DeploymentLogService : IDeploymentLogService
{
    private static ILogger Logger => Log.ForContext<DeploymentLogService>();

    public DeploymentLogSession Initialize(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A root path is required.", nameof(rootPath));
        }

        string normalizedRoot = rootPath.Trim();
        string logsDirectory = Path.Combine(normalizedRoot, "Logs");
        string stateDirectory = Path.Combine(normalizedRoot, "State");
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(stateDirectory);

        string stateFilePath = Path.Combine(stateDirectory, "deployment-state.json");

        Logger.Information(
            "Deployment log session initialized. RootPath={RootPath}, LogsDirectoryPath={LogsDirectoryPath}",
            normalizedRoot,
            logsDirectory);

        return new DeploymentLogSession
        {
            RootPath = normalizedRoot,
            LogsDirectoryPath = logsDirectory,
            StateDirectoryPath = stateDirectory,
            StateFilePath = stateFilePath
        };
    }

    public Task AppendAsync(
        DeploymentLogSession session,
        DeploymentLogLevel level,
        string message,
        CancellationToken cancellationToken = default)
    {
        LogEventLevel serilogLevel = MapLevel(level);
        try
        {
            Logger
                .ForContext("DeploymentRootPath", session.RootPath)
                .Write(
                    serilogLevel,
                    "{DeploymentMessage}",
                    LogValueSanitizer.NormalizePropertyValue(message));
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine(
                $"Foundry.Deploy session log write failed: {ex.GetType().Name}");
        }

        return Task.CompletedTask;
    }

    public async Task SaveStateAsync<TState>(
        DeploymentLogSession session,
        TState state,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        json = VolumePathDiagnostics.Redact(json);

        string temporaryStateFilePath = session.StateFilePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryStateFilePath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryStateFilePath, session.StateFilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryStateFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                global::System.Diagnostics.Debug.WriteLine(
                    $"Foundry.Deploy temporary state cleanup failed: {ex.GetType().Name}");
            }
        }
    }

    private static LogEventLevel MapLevel(DeploymentLogLevel level)
    {
        return level switch
        {
            DeploymentLogLevel.Verbose => LogEventLevel.Verbose,
            DeploymentLogLevel.Debug => LogEventLevel.Debug,
            DeploymentLogLevel.Info => LogEventLevel.Information,
            DeploymentLogLevel.Warning => LogEventLevel.Warning,
            DeploymentLogLevel.Error => LogEventLevel.Error,
            DeploymentLogLevel.Fatal => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }
}
