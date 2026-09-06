// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeProcessExecutionTests
{
    [Fact]
    public void ToDiagnosticText_IncludesCommandWorkingDirectoryExitCodeAndStreams()
    {
        var execution = new WinPeProcessExecution
        {
            FileName = "dism.exe",
            Arguments = "/?",
            WorkingDirectory = "C:\\Work",
            ExitCode = 1,
            StandardOutput = "output",
            StandardError = "error"
        };

        Assert.Equal(
            "Command: dism.exe /?\r\n" +
            "WorkingDirectory: C:\\Work\r\n" +
            "ExitCode: 1\r\n" +
            "StdOut:\r\n" +
            "output\r\n" +
            "StdErr:\r\n" +
            "error",
            execution.ToDiagnosticText());
    }

    [Fact]
    public void ToFailureDiagnostic_KeepsRawDetailsLocalAndCreatesSafeStructuredFields()
    {
        var execution = new WinPeProcessExecution
        {
            FileName = @"C:\Program Files\Windows Kits\MakeWinPEMedia.cmd",
            Arguments = "/ISO secret=plain-text",
            WorkingDirectory = @"C:\Users\operator\workspace",
            ExitCode = 7,
            StandardOutput = "raw output",
            StandardError = @"token=plain-text C:\Users\operator\private.txt"
        };

        WinPeDiagnostic diagnostic = execution.ToFailureDiagnostic(
            WinPeErrorCodes.IsoCreateFailed,
            "Failed to create WinPE ISO media.",
            "Create ISO media",
            "MakeWinPEMedia");

        Assert.Equal(WinPeFailureKinds.Process, diagnostic.FailureKind);
        Assert.Equal(WinPeFailureReasons.NonZeroExit, diagnostic.FailureReason);
        Assert.Equal("MakeWinPEMedia", diagnostic.ToolName);
        Assert.Equal(7, diagnostic.ExitCode);
        Assert.Contains("plain-text", diagnostic.Details);
        Assert.DoesNotContain("plain-text", diagnostic.ErrorSummary);
        Assert.DoesNotContain(@"C:\Users\operator", diagnostic.ErrorSummary);
    }

    [Fact]
    public void Failure_WithException_PreservesOriginalExceptionAndClassification()
    {
        var exception = new UnauthorizedAccessException("Access denied.");

        WinPeResult result = WinPeResult.Failure(
            WinPeErrorCodes.IsoCreateFailed,
            "ISO creation failed.",
            exception.Message,
            exception: exception);

        Assert.Same(exception, result.Error!.Exception);
        Assert.Equal(WinPeFailureKinds.FileSystem, result.Error.FailureKind);
        Assert.Equal(WinPeFailureReasons.AccessDenied, result.Error.FailureReason);
    }

    [Fact]
    public void Diagnostic_ClassifiesCommonFailureBoundaries()
    {
        WinPeDiagnostic httpStatus = CreateDiagnostic(
            new HttpRequestException("Not found.", null, System.Net.HttpStatusCode.NotFound));
        WinPeDiagnostic transport = CreateDiagnostic(new HttpRequestException("Connection reset."));
        WinPeDiagnostic timeout = CreateDiagnostic(new TimeoutException("Timed out."));
        WinPeDiagnostic cancelled = CreateDiagnostic(new OperationCanceledException("Cancelled."));
        var disk = new WinPeDiagnostic(WinPeErrorCodes.UsbUnsafeTarget, "Unsafe disk.");

        Assert.Equal((WinPeFailureKinds.Network, WinPeFailureReasons.HttpStatus), (httpStatus.FailureKind, httpStatus.FailureReason));
        Assert.Equal((WinPeFailureKinds.Network, WinPeFailureReasons.Transport), (transport.FailureKind, transport.FailureReason));
        Assert.Equal((WinPeFailureKinds.Network, WinPeFailureReasons.Timeout), (timeout.FailureKind, timeout.FailureReason));
        Assert.Equal((WinPeFailureKinds.Cancellation, WinPeFailureReasons.Cancelled), (cancelled.FailureKind, cancelled.FailureReason));
        Assert.Equal((WinPeFailureKinds.Validation, WinPeFailureReasons.DiskValidation), (disk.FailureKind, disk.FailureReason));
    }

    private static WinPeDiagnostic CreateDiagnostic(Exception exception) => new(
        WinPeErrorCodes.DownloadFailed,
        "Operation failed.",
        exception: exception);

}
