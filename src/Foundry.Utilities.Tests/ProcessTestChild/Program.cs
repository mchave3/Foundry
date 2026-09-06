// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text.Json;

return await ProcessTestChild.RunAsync(args);

internal static class ProcessTestChild
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChildExpiry = TimeSpan.FromMinutes(2);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "argv" => EchoArguments(args[1..]),
                "large-output" when args.Length == 2 => await WriteLargeOutputAsync(args[1] == "lines").ConfigureAwait(false),
                "pipe-root" when args.Length == 2 => await RunPipeRootAsync(args[1]).ConfigureAwait(false),
                "pipe-child" when args.Length == 2 => await RunPipeChildAsync(args[1]).ConfigureAwait(false),
                _ => 2
            };
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static int EchoArguments(string[] arguments)
    {
        Console.WriteLine(JsonSerializer.Serialize(arguments));
        return 0;
    }

    private static async Task<int> WriteLargeOutputAsync(bool useLines)
    {
        string chunk = new('x', 4096);
        for (int index = 0; index < 512; index++)
        {
            await Console.Out.WriteAsync(chunk).ConfigureAwait(false);
            await Console.Error.WriteAsync(chunk).ConfigureAwait(false);
            if (useLines)
            {
                await Console.Out.WriteLineAsync().ConfigureAwait(false);
                await Console.Error.WriteLineAsync().ConfigureAwait(false);
            }
        }

        await Console.Out.WriteLineAsync("stdout-tail").ConfigureAwait(false);
        await Console.Error.WriteLineAsync("stderr-tail").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunPipeRootAsync(string workspace)
    {
        Directory.CreateDirectory(workspace);
        using Process currentProcess = Process.GetCurrentProcess();
        WriteIdentity(Path.Combine(workspace, "root.json"), currentProcess);

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The fixture executable path is unavailable.");
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        child.StartInfo.ArgumentList.Add("pipe-child");
        child.StartInfo.ArgumentList.Add(workspace);

        if (!child.Start())
        {
            throw new InvalidOperationException("The pipe-holding child process did not start.");
        }

        WriteIdentity(Path.Combine(workspace, "child.json"), child);

        try
        {
            await WaitForFileAsync(
                Path.Combine(workspace, "child-ready"),
                ReadyTimeout,
                child,
                "The pipe-holding child did not become ready.").ConfigureAwait(false);
            Console.WriteLine("root-ready");
            await Console.Out.FlushAsync().ConfigureAwait(false);
            await WaitForFileAsync(
                Path.Combine(workspace, "allow-root-exit"),
                ReadyTimeout,
                child,
                "The test did not allow the fixture root to exit.").ConfigureAwait(false);
            return 0;
        }
        catch
        {
            File.WriteAllText(Path.Combine(workspace, "release-child"), string.Empty);
            TryKill(child);
            throw;
        }
    }

    private static async Task<int> RunPipeChildAsync(string workspace)
    {
        string readyPath = Path.Combine(workspace, "child-ready");
        string releasePath = Path.Combine(workspace, "release-child");
        File.WriteAllText(readyPath, string.Empty);
        Console.WriteLine("child-ready");
        await Console.Out.FlushAsync().ConfigureAwait(false);

        DateTimeOffset expiresAt = DateTimeOffset.UtcNow + ChildExpiry;
        while (!File.Exists(releasePath) && DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, Process child, string timeoutMessage)
    {
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (child.HasExited)
            {
                throw new InvalidOperationException($"The pipe-holding child exited with code {child.ExitCode} before becoming ready.");
            }

            if (DateTimeOffset.UtcNow >= expiresAt)
            {
                throw new TimeoutException(timeoutMessage);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }
    }

    private static void WriteIdentity(string path, Process process)
    {
        var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
        File.WriteAllText(path, JsonSerializer.Serialize(identity));
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
            // Fixture failure cleanup must not replace the original error.
        }
    }

    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks);
}
