// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Microsoft.Win32;

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeToolResolver
{
    private const string AdkRegistryPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots";
    private const string AdkRegistryKey = "KitsRoot10";

    private readonly Func<string?> _readKitsRootFromRegistry;

    public WinPeToolResolver()
        : this(ReadKitsRootFromRegistry)
    {
    }

    internal WinPeToolResolver(Func<string?> readKitsRootFromRegistry)
    {
        _readKitsRootFromRegistry = readKitsRootFromRegistry;
    }

    public WinPeResult<WinPeToolPaths> ResolveTools(string? kitsRootOverride = null)
    {
        string? kitsRoot = NormalizeKitsRoot(kitsRootOverride);
        if (string.IsNullOrWhiteSpace(kitsRoot))
        {
            kitsRoot = NormalizeKitsRoot(_readKitsRootFromRegistry());
        }

        if (string.IsNullOrWhiteSpace(kitsRoot))
        {
            return WinPeResult<WinPeToolPaths>.Failure(
                WinPeErrorCodes.ToolNotFound,
                "Could not locate Windows ADK KitsRoot10.",
                "Install ADK + WinPE add-on or provide an explicit ADK root path.",
                toolName: "Windows ADK");
        }

        string[] winPeRootCandidates =
        [
            Path.Combine(kitsRoot, "Assessment and Deployment Kit", "Windows Preinstallation Environment"),
            Path.Combine(kitsRoot, "Windows Preinstallation Environment")
        ];

        string? copypePath = ResolveToolPath(winPeRootCandidates, "copype.cmd");
        string? makeWinPeMediaPath = ResolveToolPath(winPeRootCandidates, "MakeWinPEMedia.cmd");

        if (copypePath is null || makeWinPeMediaPath is null)
        {
            return WinPeResult<WinPeToolPaths>.Failure(
                WinPeErrorCodes.ToolNotFound,
                "Required WinPE ADK tools were not found.",
                $"Expected copype.cmd and MakeWinPEMedia.cmd under '{kitsRoot}'.",
                toolName: "copype/MakeWinPEMedia");
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string dismPath = Path.Combine(windowsDirectory, "System32", "dism.exe");
        string cmdPath = Path.Combine(windowsDirectory, "System32", "cmd.exe");

        if (!File.Exists(dismPath))
        {
            return WinPeResult<WinPeToolPaths>.Failure(
                WinPeErrorCodes.ToolNotFound,
                "DISM executable was not found.",
                $"Expected path: '{dismPath}'.",
                toolName: "dism");
        }

        if (!File.Exists(cmdPath))
        {
            return WinPeResult<WinPeToolPaths>.Failure(
                WinPeErrorCodes.ToolNotFound,
                "cmd.exe was not found.",
                $"Expected path: '{cmdPath}'.",
                toolName: "cmd");
        }

        return WinPeResult<WinPeToolPaths>.Success(new WinPeToolPaths
        {
            KitsRootPath = kitsRoot,
            CopypePath = copypePath,
            MakeWinPeMediaPath = makeWinPeMediaPath,
            DismPath = dismPath,
            CmdPath = cmdPath,
            PowerShellPath = "powershell.exe"
        });
    }

    public async Task<bool> IsBootExSupportedAsync(
        WinPeToolPaths toolPaths,
        IWinPeProcessRunner processRunner,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        WinPeProcessExecution helpResult = await processRunner.RunCmdScriptDirectAsync(
            toolPaths.MakeWinPeMediaPath,
            "/?",
            workingDirectory,
            cancellationToken,
            executionTimeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);

        helpResult.EnsureCompleteOutput();
        string combined = string.Concat(helpResult.StandardOutput, "\n", helpResult.StandardError);
        return combined.IndexOf("/bootex", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? ReadKitsRootFromRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(AdkRegistryPath);
            return key?.GetValue(AdkRegistryKey) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeKitsRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string normalized = candidate.Trim().Trim('"');
        if (!Directory.Exists(normalized))
        {
            return null;
        }

        return normalized;
    }

    private static string? ResolveToolPath(IEnumerable<string> rootCandidates, string fileName)
    {
        foreach (string candidateRoot in rootCandidates)
        {
            if (!Directory.Exists(candidateRoot))
            {
                continue;
            }

            string directPath = Path.Combine(candidateRoot, fileName);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            string[] nestedCandidates = Directory.GetFiles(candidateRoot, fileName, SearchOption.AllDirectories);
            if (nestedCandidates.Length > 0)
            {
                return nestedCandidates[0];
            }
        }

        return null;
    }
}
