// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Runtime.InteropServices;
using Foundry.Deploy.Services.Deployment;
using Foundry.Utilities.Processes;

namespace Foundry.Deploy.Services.System;

/// <summary>
/// Runs archive extraction for deployment payloads through the 7-Zip executable provisioned in the WinPE image.
/// </summary>
public sealed class ArchiveExtractionService : IArchiveExtractionService
{
    private readonly IProcessRunner _processRunner;

    public ArchiveExtractionService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    /// <inheritdoc />
    public async Task ExtractWithSevenZipAsync(
        string archivePath,
        string extractedPath,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("Archive path is required.", nameof(archivePath));
        }

        if (string.IsNullOrWhiteSpace(extractedPath))
        {
            throw new ArgumentException("Extraction path is required.", nameof(extractedPath));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory is required.", nameof(workingDirectory));
        }

        string sevenZipPath = ResolveSevenZipExecutablePath();
        Directory.CreateDirectory(extractedPath);
        progress?.Report(0d);

        SevenZipProgressReporter? reporter = progress is null ? null : new(progress);
        Action<string>? onOutput = reporter is null ? null : reporter.HandleOutput;
        Action<string>? onError = reporter is null ? null : reporter.HandleOutput;
        ProcessExecutionResult execution = await _processRunner
            .RunAsync(
                sevenZipPath,
                [
                    "x",
                    "-y",
                    "-bsp1",
                    $"-o{extractedPath}",
                    archivePath
                ],
                workingDirectory,
                onOutput,
                onError,
                cancellationToken,
                TimeSpan.FromHours(4))
            .ConfigureAwait(false);

        if (!execution.IsSuccess)
        {
            throw new DeploymentProcessException(
                $"7-Zip extraction failed for '{archivePath}'.{Environment.NewLine}{execution.ToDiagnosticText()}",
                execution.ExitCode);
        }

        progress?.Report(100d);
    }

    private static string ResolveSevenZipExecutablePath()
    {
        return ResolveSevenZipExecutablePath(Environment.SystemDirectory);
    }

    /// <summary>
    /// Resolves the single supported 7-Zip executable location for Foundry.Deploy in WinPE.
    /// </summary>
    /// <param name="systemDirectory">The WinPE system directory used to derive the boot image root.</param>
    /// <returns>The architecture-specific executable under <c>Foundry\Tools\7zip</c>.</returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the boot image was not provisioned with the expected Foundry 7-Zip tool.
    /// </exception>
    internal static string ResolveSevenZipExecutablePath(string systemDirectory)
    {
        string runtimeFolder = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        string? provisionedPath = null;
        string? winPeRoot = ResolveWindowsRoot(systemDirectory);
        if (!string.IsNullOrWhiteSpace(winPeRoot))
        {
            provisionedPath = Path.Combine(winPeRoot, "Foundry", "Tools", "7zip", runtimeFolder, "7za.exe");
            if (File.Exists(provisionedPath))
            {
                return provisionedPath;
            }
        }

        throw new FileNotFoundException(
            $"No usable 7-Zip executable was found. Expected the provisioned WinPE tool at '{provisionedPath}'.",
            provisionedPath);
    }

    private static string? ResolveWindowsRoot(string systemDirectory)
    {
        DirectoryInfo? windowsDirectoryInfo = Directory.GetParent(systemDirectory);
        DirectoryInfo? windowsRootInfo = windowsDirectoryInfo?.Parent;
        return windowsRootInfo?.FullName;
    }

}
