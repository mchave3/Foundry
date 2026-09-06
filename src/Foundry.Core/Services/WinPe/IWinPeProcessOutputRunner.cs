// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

internal interface IWinPeProcessOutputRunner : IWinPeProcessRunner
{
    /// <summary>Runs independent executable arguments while forwarding bounded output segments.</summary>
    Task<WinPeProcessExecution> RunWithOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onOutputData,
        Action<string>? onErrorData,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? executionTimeout = null);

    Task<WinPeProcessExecution> RunWithOutputAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        Action<string>? onOutputData,
        Action<string>? onErrorData,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? executionTimeout = null);
}
