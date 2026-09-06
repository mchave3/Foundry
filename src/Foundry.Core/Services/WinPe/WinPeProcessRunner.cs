// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Runtime.ExceptionServices;
using Foundry.Utilities.Processes;
using UtilityProcessRunner = Foundry.Utilities.Processes.ProcessRunner;

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeProcessRunner : IWinPeProcessOutputRunner
{
    private const string InternalSetEnvKey = "FOUNDRY_ADK_SETENV_PATH";
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromHours(4);
    private readonly UtilityProcessRunner _processRunner = new();

    /// <inheritdoc />
    public Task<WinPeProcessExecution> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? executionTimeout = null) =>
        RunWithOutputAsync(fileName, arguments, workingDirectory, null, null, cancellationToken, environmentOverrides, executionTimeout);

    public async Task<WinPeProcessExecution> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? executionTimeout = null)
    {
        return await RunWithOutputAsync(
            fileName,
            arguments,
            workingDirectory,
            null,
            null,
            cancellationToken,
            environmentOverrides,
            executionTimeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<WinPeProcessExecution> RunWithOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onOutputData,
        Action<string>? onErrorData,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? executionTimeout = null) =>
        RunCoreAsync(new ProcessExecutionRequest(fileName, arguments, workingDirectory), onOutputData, onErrorData, cancellationToken, environmentOverrides, executionTimeout);

    public Task<WinPeProcessExecution> RunWithOutputAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        Action<string>? onOutputData,
        Action<string>? onErrorData,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? executionTimeout = null) =>
        RunCoreAsync(ProcessExecutionRequest.FromRawArguments(fileName, arguments, workingDirectory), onOutputData, onErrorData, cancellationToken, environmentOverrides, executionTimeout);

    private async Task<WinPeProcessExecution> RunCoreAsync(
        ProcessExecutionRequest request,
        Action<string>? onOutputData,
        Action<string>? onErrorData,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        TimeSpan? executionTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        request = request with
        {
            EnvironmentOverrides = FilterEnvironmentOverrides(environmentOverrides),
            OnOutputData = onOutputData,
            OnErrorData = onErrorData,
            ExecutionTimeout = executionTimeout ?? DefaultExecutionTimeout
        };

        try
        {
            ProcessExecutionResult result = await _processRunner
                .RunAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return WinPeProcessExecution.FromProcessExecutionResult(result);
        }
        catch (ProcessStartException ex) when (ex.InnerException is Win32Exception or InvalidOperationException)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        catch (ProcessStartException ex)
        {
            throw new InvalidOperationException($"Failed to start process '{request.FileName}'.", ex);
        }
    }

    public Task<WinPeProcessExecution> RunCmdScriptAsync(
        string scriptPath,
        string scriptArguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null)
    {
        return RunCmdScriptCoreAsync(
            scriptPath,
            scriptArguments,
            workingDirectory,
            cancellationToken,
            callTargetScript: true,
            useCommandExtensionsStripQuoteRules: true,
            executionTimeout);
    }

    public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
        string scriptPath,
        string scriptArguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null)
    {
        return RunCmdScriptCoreAsync(
            scriptPath,
            scriptArguments,
            workingDirectory,
            cancellationToken,
            callTargetScript: false,
            useCommandExtensionsStripQuoteRules: false,
            executionTimeout);
    }

    /// <summary>Quotes a batch value; expansion and control syntax are unsupported, including inside quotes.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('"'))
        {
            throw new ArgumentException("Batch paths and values cannot contain quotation marks.", nameof(value));
        }

        string quoted = $"\"{value}\"";
        ValidateBatchArguments(quoted);
        return quoted;
    }

    /// <summary>Validates batch paths and grammar before a caller changes its workspace or output files.</summary>
    internal static void ValidateCmdScript(string scriptPath, string scriptArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        _ = Quote(scriptPath);
        ValidateBatchArguments(scriptArguments);
        IReadOnlyDictionary<string, string>? environment = BuildAdkEnvironmentOverrides(scriptPath);
        if (environment is not null && environment.TryGetValue(InternalSetEnvKey, out string? setEnvPath))
        {
            _ = Quote(setEnvPath);
        }
    }

    private static void ValidateBatchArguments(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool quoted = false;
        foreach (char character in arguments)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsControl(character) || "%!^&|<>".Contains(character) ||
                     (!quoted && character is '(' or ')'))
            {
                throw new ArgumentException("Batch paths and arguments contain unsupported command syntax. Use paths without expansion or control characters.", nameof(arguments));
            }
        }

        if (quoted)
        {
            throw new ArgumentException("Batch arguments contain an unmatched quotation mark.", nameof(arguments));
        }
    }

    private Task<WinPeProcessExecution> RunCmdScriptCoreAsync(
        string scriptPath,
        string scriptArguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        bool callTargetScript,
        bool useCommandExtensionsStripQuoteRules,
        TimeSpan? executionTimeout)
    {
        ValidateCmdScript(scriptPath, scriptArguments);

        string normalizedScriptArguments = string.IsNullOrWhiteSpace(scriptArguments)
            ? string.Empty
            : $" {scriptArguments}";

        string scriptCommand = $"{Quote(scriptPath)}{normalizedScriptArguments}";
        string command = callTargetScript
            ? $"call {scriptCommand}"
            : scriptCommand;

        IReadOnlyDictionary<string, string>? environmentOverrides = BuildAdkEnvironmentOverrides(scriptPath);
        if (environmentOverrides is not null &&
            environmentOverrides.TryGetValue(InternalSetEnvKey, out string? setEnvPath) &&
            !string.IsNullOrWhiteSpace(setEnvPath))
        {
            command = $"call {Quote(setEnvPath)} >nul 2>&1 && {command}";
        }

        string switchS = useCommandExtensionsStripQuoteRules ? " /s" : string.Empty;
        string arguments = $"/d /v:off{switchS} /c \"{command}\"";
        return RunAsync(GetCommandProcessorPath(), arguments, workingDirectory, cancellationToken, environmentOverrides, executionTimeout);
    }

    private static string GetCommandProcessorPath()
    {
        string? cmdPath = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(cmdPath))
        {
            return cmdPath;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
    }

    private static IReadOnlyDictionary<string, string?>? FilterEnvironmentOverrides(
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        if (environmentOverrides is null)
        {
            return null;
        }

        var filtered = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in environmentOverrides)
        {
            if (!key.StartsWith("FOUNDRY_", StringComparison.Ordinal))
            {
                filtered[key] = value;
            }
        }

        return filtered;
    }

    private static IReadOnlyDictionary<string, string>? BuildAdkEnvironmentOverrides(string scriptPath)
    {
        string? winPeRoot = FindWinPeRootDirectory(scriptPath);
        if (winPeRoot is null)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WinPERoot"] = winPeRoot
        };

        string? adkRoot = Directory.GetParent(winPeRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(adkRoot))
        {
            return result;
        }

        string deploymentToolsRoot = Path.Combine(adkRoot, "Deployment Tools");
        if (!Directory.Exists(deploymentToolsRoot))
        {
            return result;
        }

        string[] hostArchitectureCandidates = Environment.Is64BitOperatingSystem
            ? ["amd64", "x86"]
            : ["x86", "amd64"];

        foreach (string hostArchitecture in hostArchitectureCandidates)
        {
            string hostToolsRoot = Path.Combine(deploymentToolsRoot, hostArchitecture);
            if (!Directory.Exists(hostToolsRoot))
            {
                continue;
            }

            string oscdimgRoot = Path.Combine(hostToolsRoot, "Oscdimg");
            if (Directory.Exists(oscdimgRoot))
            {
                result["OSCDImgRoot"] = oscdimgRoot;
            }

            string dismRoot = Path.Combine(hostToolsRoot, "DISM");
            if (Directory.Exists(dismRoot))
            {
                result["DISMRoot"] = dismRoot;
            }

            break;
        }

        string setEnvPath = Path.Combine(deploymentToolsRoot, "DandISetEnv.bat");
        if (File.Exists(setEnvPath))
        {
            result[InternalSetEnvKey] = setEnvPath;
        }

        return result;
    }

    private static string? FindWinPeRootDirectory(string scriptPath)
    {
        string? directoryPath = Path.GetDirectoryName(scriptPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        var current = new DirectoryInfo(directoryPath);
        while (current is not null)
        {
            if (current.Name.Equals("Windows Preinstallation Environment", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
