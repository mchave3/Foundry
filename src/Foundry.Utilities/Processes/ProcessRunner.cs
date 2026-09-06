// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Foundry.Utilities.Processes;

/// <summary>
/// Runs a process with redirected UTF-8 output and cancellation-aware tree termination.
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>
    /// Runs a process and captures its output.
    /// </summary>
    /// <remarks>
    /// Interruption exceptions report root exit and output drain confirmation in their Data dictionary.
    /// ProcessTreeTerminationConfirmed remains false because root exit cannot prove descendant termination.
    /// Callers must reconcile resources that native descendants may still own before deleting them.
    /// </remarks>
    public async Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("Executable path is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            throw new ArgumentException("Working directory is required.", nameof(request));
        }

        ValidateLimits(request);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        string argumentsDisplay;
        if (request.UsesRawArguments)
        {
            startInfo.Arguments = request.RawArguments!;
            argumentsDisplay = request.RawArguments!;
        }
        else
        {
            IReadOnlyList<string> arguments = request.ArgumentList ?? [];
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            argumentsDisplay = string.Join(" ", arguments.Select(FormatArgumentForDisplay));
        }

        ApplyEnvironmentOverrides(startInfo, request.EnvironmentOverrides);

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var stdout = new ProcessOutputCapture(request.MaxCapturedOutputCharacters, request.OnOutputData);
        var stderr = new ProcessOutputCapture(request.MaxCapturedOutputCharacters, request.OnErrorData);
        using var readCancellation = new CancellationTokenSource();
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Start(process, request.FileName);
        if (request.ExecutionTimeout is { } timeout)
        {
            executionCancellation.CancelAfter(timeout);
        }

        Task stdoutRead = stdout.ReadAsync(process.StandardOutput, readCancellation.Token);
        Task stderrRead = stderr.ReadAsync(process.StandardError, readCancellation.Token);
        Task streams = Task.WhenAll(stdoutRead, stderrRead);
        Task outputCompletion = WaitForOutputAsync(stdoutRead, stderrRead);
        try
        {
            Task rootExit = process.WaitForExitAsync(executionCancellation.Token);
            if (await Task.WhenAny(rootExit, outputCompletion).ConfigureAwait(false) == outputCompletion)
            {
                await outputCompletion.ConfigureAwait(false);
            }

            await rootExit.ConfigureAwait(false);
            await outputCompletion.WaitAsync(request.TerminationGracePeriod, executionCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            bool streamsDrained = await TerminateAsync(process, streams, request.TerminationGracePeriod).ConfigureAwait(false);
            stdout.StopCallbacks();
            stderr.StopCallbacks();
            readCancellation.Cancel();
            CloseOutput(process);
            _ = ObserveOutputAsync(streams);
            _ = ObserveOutputAsync(outputCompletion);

            Exception interruption = cancellationToken.IsCancellationRequested
                ? new OperationCanceledException("Process execution was canceled.", error, cancellationToken)
                : executionCancellation.IsCancellationRequested || error is TimeoutException
                    ? new TimeoutException("Process execution or output draining exceeded its deadline.", error)
                    : error;
            interruption.Data["ProcessRootExitConfirmed"] = HasExited(process);
            // Kill and root exit cannot prove that detached descendants have terminated.
            interruption.Data["ProcessTreeTerminationConfirmed"] = false;
            interruption.Data["ProcessOutputDrainConfirmed"] = streamsDrained;
            if (!ReferenceEquals(interruption, error))
            {
                throw interruption;
            }

            throw;
        }

        return new ProcessExecutionResult
        {
            ExitCode = process.ExitCode,
            FileName = request.FileName,
            Arguments = argumentsDisplay,
            WorkingDirectory = request.WorkingDirectory,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            StandardOutputTruncated = stdout.Truncated,
            StandardErrorTruncated = stderr.Truncated
        };
    }

    private static void ValidateLimits(ProcessExecutionRequest request)
    {
        if (request.MaxCapturedOutputCharacters is <= 0 or > 67_108_864)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Captured output must be limited to 1 through 67,108,864 characters per stream.");
        }

        TimeSpan maximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (request.TerminationGracePeriod <= TimeSpan.Zero || request.TerminationGracePeriod > maximumTimeout ||
            request.ExecutionTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > maximumTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Process deadlines must be positive, finite timer intervals.");
        }
    }

    /// <summary>Reports a reader failure promptly while its peer may still be blocked on a pipe.</summary>
    internal static async Task WaitForOutputAsync(Task standardOutput, Task standardError)
    {
        Task first = await Task.WhenAny(standardOutput, standardError).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await (ReferenceEquals(first, standardOutput) ? standardError : standardOutput).ConfigureAwait(false);
    }

    private static async Task<bool> TerminateAsync(Process process, Task streams, TimeSpan gracePeriod)
    {
        TryKill(process);
        using var cleanup = new CancellationTokenSource(gracePeriod);
        try
        {
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            await streams.WaitAsync(cleanup.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static void CloseOutput(Process process)
    {
        try
        {
            process.StandardOutput.Dispose();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
        }

        try
        {
            process.StandardError.Dispose();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
        }
    }

    private static async Task ObserveOutputAsync(Task streams)
    {
        try
        {
            await streams.ConfigureAwait(false);
        }
        catch
        {
            // Closing interrupted pipe readers may complete their pending reads after the cleanup deadline.
        }
    }

    private static void ApplyEnvironmentOverrides(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        if (environmentOverrides is null)
        {
            return;
        }

        foreach ((string name, string? value) in environmentOverrides)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Environment variable names cannot be blank.", nameof(environmentOverrides));
            }

            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }
    }

    private static string FormatArgumentForDisplay(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!argument.Any(static character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        int pendingBackslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (pendingBackslashes * 2) + 1);
                builder.Append(character);
            }
            else
            {
                builder.Append('\\', pendingBackslashes);
                builder.Append(character);
            }

            pendingBackslashes = 0;
        }

        builder.Append('\\', pendingBackslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void Start(Process process, string fileName)
    {
        try
        {
            if (!process.Start())
            {
                throw new ProcessStartException(fileName, $"Unable to start process '{fileName}'.");
            }
        }
        catch (ProcessStartException)
        {
            throw;
        }
        catch (Win32Exception ex)
        {
            throw new ProcessStartException(
                fileName,
                $"Unable to start process '{fileName}'.",
                ex.NativeErrorCode,
                ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ProcessStartException(
                fileName,
                $"Unable to start process '{fileName}'.",
                innerException: ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process termination is best effort during cancellation.
        }
    }

}
