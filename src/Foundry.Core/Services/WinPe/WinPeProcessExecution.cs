// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Utilities.Diagnostics;
using Foundry.Utilities.Processes;

namespace Foundry.Core.Services.WinPe;

public sealed record WinPeProcessExecution
{
    public int ExitCode { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool StandardOutputTruncated { get; init; }
    public bool StandardErrorTruncated { get; init; }

    public bool IsSuccess => ExitCode == 0;

    /// <summary>Rejects partial output before interpreting command metadata.</summary>
    public void EnsureCompleteOutput() => ToProcessExecutionResult().EnsureCompleteOutput();

    public string ToDiagnosticText()
    {
        return ToProcessExecutionResult().ToDiagnosticText();
    }

    public WinPeDiagnostic ToFailureDiagnostic(
        string code,
        string message,
        string? stage = null,
        string? toolName = null)
    {
        string summarySource = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        return new WinPeDiagnostic(
            code,
            message,
            ToDiagnosticText(),
            stage,
            exitCode: ExitCode,
            failureKind: WinPeFailureKinds.Process,
            failureReason: WinPeFailureReasons.NonZeroExit,
            toolName: toolName ?? Path.GetFileNameWithoutExtension(FileName),
            errorSummary: DiagnosticContentSanitizer.Sanitize(summarySource, 512));
    }

    internal static WinPeProcessExecution FromProcessExecutionResult(ProcessExecutionResult result)
    {
        return new WinPeProcessExecution
        {
            ExitCode = result.ExitCode,
            FileName = result.FileName,
            Arguments = result.Arguments,
            WorkingDirectory = result.WorkingDirectory,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            StandardOutputTruncated = result.StandardOutputTruncated,
            StandardErrorTruncated = result.StandardErrorTruncated
        };
    }

    private ProcessExecutionResult ToProcessExecutionResult()
    {
        return new ProcessExecutionResult
        {
            ExitCode = ExitCode,
            FileName = FileName,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            StandardOutput = StandardOutput,
            StandardError = StandardError,
            StandardOutputTruncated = StandardOutputTruncated,
            StandardErrorTruncated = StandardErrorTruncated
        };
    }
}
