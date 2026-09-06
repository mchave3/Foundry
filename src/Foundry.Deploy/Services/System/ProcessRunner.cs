// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging;
using UtilityProcessRunner = Foundry.Utilities.Processes.ProcessRunner;

namespace Foundry.Deploy.Services.System;

public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromHours(4);
    private readonly UtilityProcessRunner _processRunner;
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(UtilityProcessRunner processRunner, ILogger<ProcessRunner> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
    {
        ProcessExecutionRequest request = ProcessExecutionRequest.FromRawArguments(
            fileName,
            arguments,
            workingDirectory) with { ExecutionTimeout = executionTimeout ?? DefaultExecutionTimeout };

        return await RunAsync(request, arguments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
    {
        return await RunAsync(
            fileName,
            arguments,
            workingDirectory,
            onOutputData: null,
            onErrorData: null,
            cancellationToken,
            executionTimeout).ConfigureAwait(false);
    }

    public async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string>? onOutputData,
        Action<string>? onErrorData,
        CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string[] argumentList = [.. arguments];
        var request = new ProcessExecutionRequest(fileName, argumentList, workingDirectory)
        {
            ExecutionTimeout = executionTimeout ?? DefaultExecutionTimeout,
            OnOutputData = WrapCallback(onOutputData),
            OnErrorData = WrapCallback(onErrorData)
        };
        string argumentsDisplay = string.Join(
            " ",
            argumentList.Select(static argument => argument.Any(char.IsWhiteSpace)
                ? $"\"{argument}\""
                : argument));

        return await RunAsync(request, argumentsDisplay, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        string argumentsDisplay,
        CancellationToken cancellationToken)
    {
        if (argumentsDisplay.Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase))
        {
            argumentsDisplay = "[encoded PowerShell command omitted]";
        }

        _logger.LogDebug(
            "Starting process. FileName={FileName}, Arguments={Arguments}, WorkingDirectory={WorkingDirectory}",
            request.FileName,
            argumentsDisplay,
            request.WorkingDirectory);

        ProcessExecutionResult result = await _processRunner
            .RunAsync(request, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Process completed. FileName={FileName}, ExitCode={ExitCode}",
            request.FileName,
            result.ExitCode);
        return result;
    }

    private Action<string>? WrapCallback(Action<string>? callback)
    {
        if (callback is null)
        {
            return null;
        }

        return data =>
        {
            try
            {
                callback(data);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Process output callback failed.");
            }
        };
    }
}
