// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeMountSession : IAsyncDisposable
{
    private readonly IWinPeProcessRunner _processRunner;
    private readonly string _dismPath;
    private readonly string _workingDirectory;
    private bool _isMounted;

    private WinPeMountSession(
        IWinPeProcessRunner processRunner,
        string dismPath,
        string bootWimPath,
        string mountDirectoryPath,
        string workingDirectory)
    {
        _processRunner = processRunner;
        _dismPath = dismPath;
        BootWimPath = bootWimPath;
        MountDirectoryPath = mountDirectoryPath;
        _workingDirectory = workingDirectory;
        _isMounted = true;
    }

    public string BootWimPath { get; }
    public string MountDirectoryPath { get; }

    public static async Task<WinPeResult<WinPeMountSession>> MountAsync(
        IWinPeProcessRunner processRunner,
        string dismPath,
        string bootWimPath,
        string mountDirectoryPath,
        string workingDirectory,
        CancellationToken cancellationToken,
        IProgress<WinPeDismProgress>? dismProgress = null)
    {
        Directory.CreateDirectory(mountDirectoryPath);

        string[] args = ["/Mount-Image", $"/ImageFile:{bootWimPath}", "/Index:1", $"/MountDir:{mountDirectoryPath}"];
        WinPeProcessExecution mountResult = await WinPeDismProcessRunner.RunAsync(
            processRunner,
            dismPath,
            args,
            workingDirectory,
            "Mounting image with DISM.",
            dismProgress,
            cancellationToken).ConfigureAwait(false);

        if (!mountResult.IsSuccess)
        {
            return WinPeResult<WinPeMountSession>.Failure(
                WinPeErrorCodes.WimMountFailed,
                "Failed to mount boot.wim.",
                mountResult.ToDiagnosticText());
        }

        return WinPeResult<WinPeMountSession>.Success(new WinPeMountSession(
            processRunner,
            dismPath,
            bootWimPath,
            mountDirectoryPath,
            workingDirectory));
    }

    public async Task<WinPeResult> CommitAsync(
        CancellationToken cancellationToken,
        IProgress<WinPeDismProgress>? dismProgress = null)
    {
        if (!_isMounted)
        {
            return WinPeResult.Success();
        }

        WinPeProcessExecution commitResult = await WinPeDismProcessRunner.RunAsync(
            _processRunner,
            _dismPath,
            ["/Unmount-Image", $"/MountDir:{MountDirectoryPath}", "/Commit"],
            _workingDirectory,
            "Committing image changes with DISM.",
            dismProgress,
            cancellationToken).ConfigureAwait(false);

        if (commitResult.IsSuccess)
        {
            _isMounted = false;
            return WinPeResult.Success();
        }

        WinPeProcessExecution discardResult = await _processRunner.RunAsync(
            _dismPath,
            ["/Unmount-Image", $"/MountDir:{MountDirectoryPath}", "/Discard"],
            _workingDirectory,
            cancellationToken).ConfigureAwait(false);

        _isMounted = false;

        string details = string.Join(
            Environment.NewLine,
            "Commit failed and discard fallback was attempted.",
            "Commit diagnostics:",
            commitResult.ToDiagnosticText(),
            "Discard diagnostics:",
            discardResult.ToDiagnosticText());

        return WinPeResult.Failure(
            WinPeErrorCodes.WimUnmountFailed,
            "Failed to commit mounted boot.wim changes.",
            details);
    }

    public async Task<WinPeResult> DiscardAsync(CancellationToken cancellationToken)
    {
        if (!_isMounted)
        {
            return WinPeResult.Success();
        }

        WinPeProcessExecution discardResult = await _processRunner.RunAsync(
            _dismPath,
            ["/Unmount-Image", $"/MountDir:{MountDirectoryPath}", "/Discard"],
            _workingDirectory,
            cancellationToken).ConfigureAwait(false);

        _isMounted = false;
        if (discardResult.IsSuccess)
        {
            return WinPeResult.Success();
        }

        return WinPeResult.Failure(
            WinPeErrorCodes.WimUnmountFailed,
            "Failed to discard mounted boot.wim changes.",
            discardResult.ToDiagnosticText());
    }

    public async ValueTask DisposeAsync()
    {
        if (_isMounted)
        {
            await DiscardAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
