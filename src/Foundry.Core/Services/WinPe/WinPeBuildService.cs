// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

/// <summary>
/// Creates WinPE workspaces by invoking ADK Copype and preparing Foundry working folders.
/// </summary>
public sealed class WinPeBuildService : IWinPeBuildService
{
    private readonly WinPeToolResolver _toolResolver;
    private readonly IWinPeProcessRunner _processRunner;

    /// <summary>
    /// Initializes a WinPE build service using the default ADK tool resolver and process runner.
    /// </summary>
    public WinPeBuildService()
        : this(new WinPeToolResolver(), new WinPeProcessRunner())
    {
    }

    internal WinPeBuildService(WinPeToolResolver toolResolver, IWinPeProcessRunner processRunner)
    {
        _toolResolver = toolResolver;
        _processRunner = processRunner;
    }

    /// <inheritdoc />
    public async Task<WinPeResult<WinPeBuildArtifact>> BuildAsync(
        WinPeBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WinPeDiagnostic? validationError = ValidateBuildOptions(options);
        if (validationError is not null)
        {
            return WinPeResult<WinPeBuildArtifact>.Failure(validationError);
        }

        WinPeResult<WinPeToolPaths> toolsResult = _toolResolver.ResolveTools(ResolveAdkRootHint(options));
        if (!toolsResult.IsSuccess)
        {
            return WinPeResult<WinPeBuildArtifact>.Failure(toolsResult.Error!);
        }

        WinPeToolPaths tools = toolsResult.Value!;
        string workingDirectory = ResolveWorkingDirectory(options);

        try
        {
            string arguments = $"{options.Architecture.ToCopypeArchitecture()} {WinPeProcessRunner.Quote(workingDirectory)}";
            WinPeProcessRunner.ValidateCmdScript(tools.CopypePath, arguments);

            if (Directory.Exists(workingDirectory) && options.CleanExistingWorkingDirectory)
            {
                // The workspace is owned by this build stage, so stale ADK output is removed before Copype runs.
                Directory.Delete(workingDirectory, recursive: true);
            }

            Directory.CreateDirectory(options.OutputDirectoryPath);

            WinPeProcessExecution copyPeResult = await _processRunner.RunCmdScriptAsync(
                tools.CopypePath,
                arguments,
                options.OutputDirectoryPath,
                cancellationToken).ConfigureAwait(false);

            if (!copyPeResult.IsSuccess)
            {
                return WinPeResult<WinPeBuildArtifact>.Failure(copyPeResult.ToFailureDiagnostic(
                    WinPeErrorCodes.BuildFailed,
                    "Failed to create WinPE workspace using copype.cmd.",
                    "Build WinPE workspace",
                    "copype"));
            }

            string mediaDirectory = Path.Combine(workingDirectory, "media");
            string bootWimPath = Path.Combine(mediaDirectory, "sources", "boot.wim");
            if (!File.Exists(bootWimPath))
            {
                return WinPeResult<WinPeBuildArtifact>.Failure(
                    WinPeErrorCodes.BuildFailed,
                    "WinPE workspace was created but boot.wim was not found.",
                    $"Expected path: '{bootWimPath}'.");
            }

            string mountDirectory = Path.Combine(workingDirectory, "mount");
            string driverWorkspace = Path.Combine(workingDirectory, "drivers");
            string logsDirectory = Path.Combine(workingDirectory, "logs");
            string tempDirectory = Path.Combine(workingDirectory, "temp");

            Directory.CreateDirectory(mountDirectory);
            Directory.CreateDirectory(driverWorkspace);
            Directory.CreateDirectory(logsDirectory);
            Directory.CreateDirectory(tempDirectory);

            return WinPeResult<WinPeBuildArtifact>.Success(new WinPeBuildArtifact
            {
                WorkingDirectoryPath = workingDirectory,
                MediaDirectoryPath = mediaDirectory,
                BootWimPath = bootWimPath,
                MountDirectoryPath = mountDirectory,
                DriverWorkspacePath = driverWorkspace,
                LogsDirectoryPath = logsDirectory,
                MakeWinPeMediaPath = tools.MakeWinPeMediaPath,
                DismPath = tools.DismPath,
                Architecture = options.Architecture,
                SignatureMode = options.SignatureMode
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WinPeResult<WinPeBuildArtifact>.Failure(
                WinPeErrorCodes.BuildFailed,
                "Unexpected failure while creating the WinPE workspace.",
                ex.ToString(),
                stage: "Build WinPE workspace",
                failureKind: ex is TimeoutException ? WinPeFailureKinds.Process : null,
                failureReason: ex switch
                {
                    TimeoutException => WinPeFailureReasons.Timeout,
                    UnauthorizedAccessException => WinPeFailureReasons.AccessDenied,
                    _ => WinPeFailureReasons.ProcessStartFailed
                },
                toolName: "copype",
                errorSummary: ex.Message,
                exception: ex);
        }
    }

    private static WinPeDiagnostic? ValidateBuildOptions(WinPeBuildOptions? options)
    {
        if (options is null)
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Build options are required.",
                "Provide a non-null WinPeBuildOptions instance.");
        }

        if (string.IsNullOrWhiteSpace(options.OutputDirectoryPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Output directory path is required.",
                "Set WinPeBuildOptions.OutputDirectoryPath to a writable destination folder.");
        }

        if (!Enum.IsDefined(options.Architecture))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Architecture value is invalid.",
                $"Value: '{options.Architecture}'.");
        }

        if (!Enum.IsDefined(options.SignatureMode))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Signature mode value is invalid.",
                $"Value: '{options.SignatureMode}'.");
        }

        return null;
    }

    private static string ResolveAdkRootHint(WinPeBuildOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AdkRootPath))
        {
            return options.AdkRootPath;
        }

        return options.SourceDirectoryPath;
    }

    private static string ResolveWorkingDirectory(WinPeBuildOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectoryPath))
        {
            return options.WorkingDirectoryPath;
        }

        string folderName = $"FoundryWinPe_{options.Architecture.ToString().ToLowerInvariant()}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        return Path.Combine(options.OutputDirectoryPath, folderName);
    }
}
