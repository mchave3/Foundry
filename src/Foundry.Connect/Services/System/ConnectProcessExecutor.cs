// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging;

namespace Foundry.Connect.Services.System;

/// <summary>Runs Connect commands with bounded execution while preserving cancellation and timeout metadata.</summary>
internal sealed class ConnectProcessExecutor(ILogger logger)
{
    private readonly ProcessRunner _processRunner = new();

    /// <summary>Preserves an explicit raw shell contract and defaults to a two-minute execution deadline.</summary>
    public Task<ProcessExecutionResult> ExecuteAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null)
    {
        ProcessExecutionRequest request = ProcessExecutionRequest.FromRawArguments(
            fileName,
            arguments,
            Environment.CurrentDirectory) with
        { ExecutionTimeout = executionTimeout ?? TimeSpan.FromMinutes(2) };

        return ExecuteAsync(request, cancellationToken);
    }

    /// <summary>Preserves direct executable tokens and defaults to a two-minute execution deadline.</summary>
    public Task<ProcessExecutionResult> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null)
    {
        var request = new ProcessExecutionRequest(fileName, arguments, Environment.CurrentDirectory)
        {
            ExecutionTimeout = executionTimeout ?? TimeSpan.FromMinutes(2)
        };

        return ExecuteAsync(request, cancellationToken);
    }

    private async Task<ProcessExecutionResult> ExecuteAsync(ProcessExecutionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Process execution failed. FileName={FileName}, FailureType={FailureType}",
                Path.GetFileName(request.FileName),
                ex.GetType().Name);
            return new ProcessExecutionResult
            {
                ExitCode = -1,
                FileName = request.FileName,
                Arguments = request.RawArguments ?? string.Join(" ", request.ArgumentList!),
                WorkingDirectory = request.WorkingDirectory,
                StandardError = ex.Message
            };
        }
    }
}
