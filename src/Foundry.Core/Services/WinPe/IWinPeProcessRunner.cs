// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

public interface IWinPeProcessRunner
{
    /// <summary>Runs an executable with independent argument tokens and a finite operation deadline.</summary>
    Task<WinPeProcessExecution> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? executionTimeout = null);

    /// <summary>Runs an explicitly constructed raw command line for compatibility.</summary>
    Task<WinPeProcessExecution> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? executionTimeout = null);

    Task<WinPeProcessExecution> RunCmdScriptAsync(
        string scriptPath,
        string scriptArguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null);

    Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
        string scriptPath,
        string scriptArguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null);
}
