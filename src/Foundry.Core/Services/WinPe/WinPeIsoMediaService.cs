// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Utilities.IO;

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeIsoMediaService : IWinPeIsoMediaService
{
    private readonly IWinPeProcessRunner _processRunner;

    public WinPeIsoMediaService()
        : this(new WinPeProcessRunner())
    {
    }

    internal WinPeIsoMediaService(IWinPeProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<WinPeResult> CreateAsync(
        WinPeIsoMediaOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WinPeDiagnostic? validationError = ValidateOptions(options);
        if (validationError is not null)
        {
            return WinPeResult.Failure(validationError);
        }

        WinPeWorkspacePreparationResult preparedWorkspace = options.PreparedWorkspace!;
        string requestedOutputPath = options.OutputIsoPath.Trim();
        string? preparedOutputPath = null;
        string? safeWorkspacePath = null;
        string currentStage = "Prepare ISO output path";

        try
        {
            WinPeProcessRunner.ValidateCmdScript(
                preparedWorkspace.Tools.MakeWinPeMediaPath,
                $"/ISO /F {WinPeProcessRunner.Quote(preparedWorkspace.Artifact.WorkingDirectoryPath)} {WinPeProcessRunner.Quote(requestedOutputPath)}");
            if (ContainsNonAscii(requestedOutputPath) || ContainsNonAscii(preparedWorkspace.Artifact.WorkingDirectoryPath))
            {
                _ = WinPeProcessRunner.Quote(options.IsoTempDirectoryPath);
            }

            ReportProgress(options.Progress, 0, "Preparing ISO output path.");
            EnsureOutputDirectoryExists(requestedOutputPath);
            preparedOutputPath = PrepareOutputPath(requestedOutputPath, options.IsoTempDirectoryPath);
            currentStage = "Prepare ISO workspace";
            ReportProgress(options.Progress, 20, "Preparing ISO workspace.");
            string makeWinPeMediaWorkspacePath = PrepareWorkspacePath(
                preparedWorkspace.Artifact.WorkingDirectoryPath,
                options.IsoTempDirectoryPath,
                out safeWorkspacePath);

            if (options.ForceOverwriteOutput && File.Exists(preparedOutputPath))
            {
                File.Delete(preparedOutputPath);
            }

            string arguments =
                $"/ISO /F {WinPeProcessRunner.Quote(makeWinPeMediaWorkspacePath)} {WinPeProcessRunner.Quote(preparedOutputPath)}" +
                (preparedWorkspace.UseBootEx ? " /bootex" : string.Empty);

            currentStage = "Run MakeWinPEMedia for ISO";
            ReportProgress(options.Progress, 40, "Running MakeWinPEMedia for ISO.");
            WinPeProcessExecution execution = await _processRunner.RunCmdScriptAsync(
                preparedWorkspace.Tools.MakeWinPeMediaPath,
                arguments,
                makeWinPeMediaWorkspacePath,
                cancellationToken).ConfigureAwait(false);

            if (!execution.IsSuccess)
            {
                return WinPeResult.Failure(execution.ToFailureDiagnostic(
                    WinPeErrorCodes.IsoCreateFailed,
                    "Failed to create WinPE ISO media.",
                    "Create ISO media",
                    "MakeWinPEMedia"));
            }

            if (!File.Exists(preparedOutputPath))
            {
                return WinPeResult.Failure(
                    WinPeErrorCodes.IsoCreateFailed,
                    "MakeWinPEMedia completed without producing the expected ISO artifact.",
                    execution.ToDiagnosticText(),
                    stage: "Create ISO media",
                    failureKind: WinPeFailureKinds.Process,
                    failureReason: WinPeFailureReasons.ArtifactMissing,
                    toolName: "MakeWinPEMedia",
                    errorSummary: "Expected ISO artifact was not produced.");
            }

            currentStage = "Finalize ISO output";
            ReportProgress(options.Progress, 90, "Finalizing ISO output.");
            FinalizeOutput(preparedOutputPath, requestedOutputPath);
            ReportProgress(options.Progress, 100, "ISO media completed.");
            return WinPeResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.IsoCreateFailed,
                "Unexpected failure while creating WinPE ISO media.",
                ex.ToString(),
                stage: currentStage,
                failureKind: currentStage == "Run MakeWinPEMedia for ISO"
                    ? WinPeFailureKinds.Process
                    : WinPeFailureKinds.FileSystem,
                failureReason: ex switch
                {
                    TimeoutException when currentStage == "Run MakeWinPEMedia for ISO" => WinPeFailureReasons.Timeout,
                    UnauthorizedAccessException => WinPeFailureReasons.AccessDenied,
                    IOException => WinPeFailureReasons.IoError,
                    _ when currentStage == "Run MakeWinPEMedia for ISO" => WinPeFailureReasons.ProcessStartFailed,
                    _ => WinPeFailureReasons.Unexpected
                },
                toolName: currentStage == "Run MakeWinPEMedia for ISO" ? "MakeWinPEMedia" : null,
                errorSummary: ex.Message,
                exception: ex);
        }
        finally
        {
            CleanupPreparedOutput(requestedOutputPath, preparedOutputPath);
            CleanupPreparedWorkspace(safeWorkspacePath);
        }
    }

    private static WinPeDiagnostic? ValidateOptions(WinPeIsoMediaOptions? options)
    {
        if (options is null)
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "ISO media options are required.",
                "Provide a non-null WinPeIsoMediaOptions instance.");
        }

        if (options.PreparedWorkspace is null)
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Prepared WinPE workspace is required.",
                "Set WinPeIsoMediaOptions.PreparedWorkspace.");
        }

        if (string.IsNullOrWhiteSpace(options.PreparedWorkspace.Tools.MakeWinPeMediaPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "MakeWinPEMedia path is required.",
                "Set WinPeToolPaths.MakeWinPeMediaPath.");
        }

        if (string.IsNullOrWhiteSpace(options.PreparedWorkspace.Artifact.WorkingDirectoryPath) ||
            !Directory.Exists(options.PreparedWorkspace.Artifact.WorkingDirectoryPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Prepared WinPE workspace directory was not found.",
                $"Path: '{options.PreparedWorkspace.Artifact.WorkingDirectoryPath}'.");
        }

        if (string.IsNullOrWhiteSpace(options.OutputIsoPath) ||
            !options.OutputIsoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Output ISO path must end with .iso.",
                $"Path: '{options.OutputIsoPath}'.");
        }

        if ((ContainsNonAscii(options.OutputIsoPath) ||
             ContainsNonAscii(options.PreparedWorkspace.Artifact.WorkingDirectoryPath)) &&
            string.IsNullOrWhiteSpace(options.IsoTempDirectoryPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "ISO temporary directory path is required for non-ASCII MakeWinPEMedia paths.",
                "Set WinPeIsoMediaOptions.IsoTempDirectoryPath.");
        }

        return null;
    }

    private static string PrepareWorkspacePath(
        string requestedWorkspacePath,
        string isoTempDirectoryPath,
        out string? safeWorkspacePath)
    {
        safeWorkspacePath = null;
        if (!ContainsNonAscii(requestedWorkspacePath))
        {
            return requestedWorkspacePath;
        }

        Directory.CreateDirectory(isoTempDirectoryPath);
        safeWorkspacePath = Path.Combine(isoTempDirectoryPath, $"workspace-{Guid.NewGuid():N}");
        CopyDirectoryContents(requestedWorkspacePath, safeWorkspacePath);
        return safeWorkspacePath;
    }

    private static string PrepareOutputPath(string requestedOutputPath, string isoTempDirectoryPath)
    {
        if (!ContainsNonAscii(requestedOutputPath))
        {
            return requestedOutputPath;
        }

        Directory.CreateDirectory(isoTempDirectoryPath);
        string fileName = Path.GetFileName(requestedOutputPath);
        string safeFileName = string.IsNullOrWhiteSpace(fileName)
            ? $"foundry-winpe-{DateTime.UtcNow:yyyyMMddHHmmssfff}.iso"
            : ToAsciiSafeFileName(fileName);

        if (!safeFileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            safeFileName += ".iso";
        }

        return Path.Combine(isoTempDirectoryPath, safeFileName);
    }

    private static void FinalizeOutput(string preparedOutputPath, string requestedOutputPath)
    {
        if (string.Equals(preparedOutputPath, requestedOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureOutputDirectoryExists(requestedOutputPath);
        File.Copy(preparedOutputPath, requestedOutputPath, overwrite: true);
    }

    private static void CleanupPreparedOutput(string requestedOutputPath, string? preparedOutputPath)
    {
        if (string.IsNullOrWhiteSpace(preparedOutputPath) ||
            string.Equals(preparedOutputPath, requestedOutputPath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(preparedOutputPath))
        {
            return;
        }

        try
        {
            File.Delete(preparedOutputPath);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void CleanupPreparedWorkspace(string? safeWorkspacePath)
    {
        if (string.IsNullOrWhiteSpace(safeWorkspacePath) || !Directory.Exists(safeWorkspacePath))
        {
            return;
        }

        try
        {
            Directory.Delete(safeWorkspacePath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void CopyDirectoryContents(string sourceDirectoryPath, string targetDirectoryPath)
    {
        Directory.CreateDirectory(targetDirectoryPath);

        foreach (string directoryPath in Directory.EnumerateDirectories(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectoryPath, directoryPath);
            Directory.CreateDirectory(Path.Combine(targetDirectoryPath, relativePath));
        }

        foreach (string filePath in Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectoryPath, filePath);
            string targetFilePath = Path.Combine(targetDirectoryPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
            File.Copy(filePath, targetFilePath, overwrite: true);
        }
    }

    private static void EnsureOutputDirectoryExists(string outputIsoPath)
    {
        string? outputDirectory = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
    }

    private static bool ContainsNonAscii(string value)
    {
        return value.Any(character => character > 127);
    }

    private static void ReportProgress(IProgress<WinPeMediaProgress>? progress, int percent, string status)
    {
        progress?.Report(new WinPeMediaProgress
        {
            Percent = Math.Clamp(percent, 0, 100),
            Status = status
        });
    }

    private static string ToAsciiSafeFileName(string fileName)
    {
        string sanitized = PathSegment.Sanitize(fileName);
        char[] chars = sanitized
            .Select(character => character > 127 ? '_' : character)
            .ToArray();

        return new string(chars);
    }
}
