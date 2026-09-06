// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace Foundry.Utilities.Processes;

/// <summary>
/// Contains the captured outcome of a completed process.
/// </summary>
public sealed record ProcessExecutionResult
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

    /// <summary>Rejects partial command output before a consumer parses metadata from it.</summary>
    public void EnsureCompleteOutput()
    {
        if (StandardOutputTruncated || StandardErrorTruncated)
        {
            throw new InvalidDataException("Process output exceeded the capture limit; complete output is required.");
        }
    }

    /// <summary>
    /// Formats a local diagnostic containing command metadata and non-empty captured streams.
    /// </summary>
    public string ToDiagnosticText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Command: {FileName} {Arguments}".TrimEnd());
        builder.AppendLine($"WorkingDirectory: {WorkingDirectory}");
        builder.AppendLine($"ExitCode: {ExitCode}");

        if (StandardOutputTruncated || !string.IsNullOrWhiteSpace(StandardOutput))
        {
            builder.AppendLine(StandardOutputTruncated ? "StdOut (truncated tail):" : "StdOut:");
            builder.AppendLine(StandardOutput.Trim());
        }

        if (StandardErrorTruncated || !string.IsNullOrWhiteSpace(StandardError))
        {
            builder.AppendLine(StandardErrorTruncated ? "StdErr (truncated tail):" : "StdErr:");
            builder.AppendLine(StandardError.Trim());
        }

        return builder.ToString().Trim();
    }
}
