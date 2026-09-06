// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

internal static class WinPeDismProcessRunner
{
    public static Task<WinPeProcessExecution> RunAsync(
        IWinPeProcessRunner processRunner,
        string dismPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string progressStatus,
        IProgress<WinPeDismProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null)
    {
        if (progress is not null && processRunner is IWinPeProcessOutputRunner outputRunner)
        {
            var reporter = new WinPeDismProgressReporter(progressStatus, progress);
            return outputRunner.RunWithOutputAsync(
                dismPath,
                arguments,
                workingDirectory,
                reporter.HandleOutput,
                reporter.HandleOutput,
                cancellationToken,
                executionTimeout: executionTimeout ?? TimeSpan.FromHours(4));
        }

        return processRunner.RunAsync(
            dismPath,
            arguments,
            workingDirectory,
            cancellationToken,
            executionTimeout: executionTimeout ?? TimeSpan.FromHours(4));
    }
}
