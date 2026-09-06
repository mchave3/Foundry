// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using Foundry.Utilities.IO;
using Foundry.Utilities.Progress;

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeDriverPackageService : IWinPeDriverPackageService
{
    private const string BundledSevenZipRelativePath = @"Assets\7z";

    private readonly IWinPeProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly string? _sevenZipExecutablePath;

    public WinPeDriverPackageService()
        : this(new WinPeProcessRunner(), CreateHttpClient(), null)
    {
    }

    internal WinPeDriverPackageService(
        IWinPeProcessRunner processRunner,
        HttpClient httpClient,
        string? sevenZipExecutablePath)
    {
        _processRunner = processRunner;
        _httpClient = httpClient;
        _sevenZipExecutablePath = sevenZipExecutablePath;
    }

    public async Task<WinPeResult<WinPePreparedDriverSet>> PrepareAsync(
        IReadOnlyList<WinPeDriverCatalogEntry> packages,
        string downloadRootPath,
        string extractRootPath,
        IProgress<WinPeDownloadProgress>? downloadProgress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(downloadRootPath);
        Directory.CreateDirectory(extractRootPath);

        var extractedDirectories = new List<string>(packages.Count);
        var downloadedFiles = new List<string>(packages.Count);

        for (int index = 0; index < packages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WinPeDriverCatalogEntry package = packages[index];
            string fileName = ResolvePackageFileName(package);
            string downloadPath = Path.Combine(downloadRootPath, fileName);

            WinPeResult downloadResult = await DownloadPackageAsync(
                package.DownloadUri,
                downloadPath,
                index + 1,
                packages.Count,
                downloadProgress,
                cancellationToken).ConfigureAwait(false);

            if (!downloadResult.IsSuccess)
            {
                return WinPeResult<WinPePreparedDriverSet>.Failure(downloadResult.Error!);
            }

            WinPeResult hashValidationResult = await ValidateSha256Async(
                package,
                downloadPath,
                cancellationToken).ConfigureAwait(false);

            if (!hashValidationResult.IsSuccess)
            {
                return WinPeResult<WinPePreparedDriverSet>.Failure(hashValidationResult.Error!);
            }

            downloadedFiles.Add(downloadPath);

            string normalizedFolderName = $"{index + 1:D2}_{PathSegment.Sanitize(package.Vendor.ToString())}_{PathSegment.Sanitize(package.Id)}";
            string extractPath = Path.Combine(extractRootPath, normalizedFolderName);
            DirectoryOperations.Recreate(extractPath);

            WinPeResult extractionResult = await ExtractPackageAsync(
                downloadPath,
                extractPath,
                cancellationToken).ConfigureAwait(false);

            if (!extractionResult.IsSuccess)
            {
                return WinPeResult<WinPePreparedDriverSet>.Failure(extractionResult.Error!);
            }

            extractedDirectories.Add(extractPath);
        }

        return WinPeResult<WinPePreparedDriverSet>.Success(new WinPePreparedDriverSet
        {
            ExtractionDirectories = extractedDirectories,
            DownloadedPackagePaths = downloadedFiles
        });
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Foundry/1.0");
        return client;
    }

    private static string ResolvePackageFileName(WinPeDriverCatalogEntry package)
    {
        if (!string.IsNullOrWhiteSpace(package.FileName))
        {
            return PathSegment.Sanitize(package.FileName);
        }

        if (Uri.TryCreate(package.DownloadUri, UriKind.Absolute, out Uri? uri))
        {
            string candidate = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return PathSegment.Sanitize(candidate);
            }
        }

        string extension = package.Format.ToLowerInvariant() switch
        {
            "cab" => ".cab",
            "zip" => ".zip",
            _ => ".exe"
        };

        return $"{PathSegment.Sanitize(package.Id)}{extension}";
    }

    private async Task<WinPeResult> DownloadPackageAsync(
        string sourceUri,
        string destinationPath,
        int packageNumber,
        int packageCount,
        IProgress<WinPeDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUri, UriKind.Absolute, out Uri? uri))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.DownloadFailed,
                "Driver package download URI is invalid.",
                sourceUri);
        }

        try
        {
            string status = BuildDriverDownloadStatus(destinationPath, packageNumber, packageCount);
            ReportDownloadProgress(progress, 0, status);

            using HttpResponseMessage response = await _httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return WinPeResult.Failure(
                    WinPeErrorCodes.DownloadFailed,
                    "Driver package download failed.",
                    $"URI: '{sourceUri}', HTTP status: {(int)response.StatusCode} {response.ReasonPhrase}",
                    failureKind: WinPeFailureKinds.Network,
                    failureReason: WinPeFailureReasons.HttpStatus,
                    errorSummary: $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            long? totalBytes = response.Content.Headers.ContentLength;
            await using Stream sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream destinationStream = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            await CopyDownloadToFileAsync(
                sourceStream,
                destinationStream,
                totalBytes,
                status,
                progress,
                cancellationToken).ConfigureAwait(false);
            return WinPeResult.Success();
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.DownloadFailed,
                "Failed to download driver package.",
                $"URI: '{sourceUri}'.{Environment.NewLine}{ex}",
                failureKind: ex is UnauthorizedAccessException ? WinPeFailureKinds.FileSystem : WinPeFailureKinds.Network,
                failureReason: ex switch
                {
                    TaskCanceledException => WinPeFailureReasons.Timeout,
                    UnauthorizedAccessException => WinPeFailureReasons.AccessDenied,
                    HttpRequestException { StatusCode: not null } => WinPeFailureReasons.HttpStatus,
                    _ => WinPeFailureReasons.Transport
                },
                errorSummary: ex.Message,
                exception: ex);
        }
    }

    private static async Task CopyDownloadToFileAsync(
        Stream sourceStream,
        FileStream destinationStream,
        long? totalBytes,
        string status,
        IProgress<WinPeDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        int lastReportedPercent = -1;
        long bytesWritten = await StreamCopy.CopyAsync(
            sourceStream,
            destinationStream,
            copiedBytes =>
            {
                double? percentage = TransferProgress.CalculatePercentage(copiedBytes, totalBytes);
                if (percentage.HasValue)
                {
                    int downloadPercent = (int)percentage.Value;
                    if (downloadPercent == lastReportedPercent)
                    {
                        return;
                    }

                    lastReportedPercent = downloadPercent;
                    ReportDownloadProgress(
                        progress,
                        downloadPercent,
                        $"{status} ({FormatBytes(copiedBytes)} / {FormatBytes(totalBytes.GetValueOrDefault())})");
                    return;
                }

                ReportDownloadProgress(
                    progress,
                    null,
                    $"{status} ({FormatBytes(copiedBytes)} downloaded)");
            },
            cancellationToken).ConfigureAwait(false);

        ReportDownloadProgress(
            progress,
            100,
            totalBytes is > 0
                ? $"{status} ({FormatBytes(bytesWritten)} / {FormatBytes(totalBytes.Value)})"
                : $"{status} ({FormatBytes(bytesWritten)} downloaded)");
    }

    private static string BuildDriverDownloadStatus(string destinationPath, int packageNumber, int packageCount)
    {
        return $"Downloading driver package {packageNumber} of {packageCount}: {Path.GetFileName(destinationPath)}.";
    }

    private static void ReportDownloadProgress(IProgress<WinPeDownloadProgress>? progress, int? percent, string status)
    {
        progress?.Report(new WinPeDownloadProgress
        {
            Percent = percent.HasValue
                ? Math.Clamp(percent.Value, 0, 100)
                : null,
            Status = status
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:F1} {units[unitIndex]}";
    }

    private static async Task<WinPeResult> ValidateSha256Async(
        WinPeDriverCatalogEntry package,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(package.Sha256))
        {
            return WinPeResult.Success();
        }

        string expected = package.Sha256.Trim().Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        string actual = await FileHash.ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return WinPeResult.Success();
        }

        return WinPeResult.Failure(
            WinPeErrorCodes.HashMismatch,
            "Driver package hash verification failed.",
            $"File: '{filePath}', Expected SHA256: '{expected}', Actual SHA256: '{actual}'.");
    }

    private async Task<WinPeResult> ExtractPackageAsync(
        string packagePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(packagePath);
        if (!extension.Equals(".cab", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.DriverExtractionFailed,
                "Unsupported driver package format.",
                $"File: '{packagePath}', extension: '{extension}'. Supported: .cab, .exe, .zip");
        }

        WinPeResult<string> sevenZipExecutablePathResult = ResolveBundledSevenZipExecutablePath();
        if (!sevenZipExecutablePathResult.IsSuccess)
        {
            return WinPeResult.Failure(sevenZipExecutablePathResult.Error!);
        }

        WinPeProcessExecution extractionResult = await _processRunner.RunAsync(
            sevenZipExecutablePathResult.Value!,
            ["x", "-y", $"-o{destinationPath}", packagePath],
            destinationPath,
            cancellationToken).ConfigureAwait(false);

        if (!extractionResult.IsSuccess)
        {
            return WinPeResult.Failure(extractionResult.ToFailureDiagnostic(
                WinPeErrorCodes.DriverExtractionFailed,
                "Failed to extract driver package with bundled 7-Zip.",
                toolName: "7-Zip"));
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !FileSearch.ContainsRecursive(destinationPath, "*.inf"))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.DriverExtractionFailed,
                "Executable driver package was extracted with 7-Zip but no INF files were found.",
                $"Archive: '{packagePath}', destination: '{destinationPath}'.");
        }

        return WinPeResult.Success();
    }

    private WinPeResult<string> ResolveBundledSevenZipExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_sevenZipExecutablePath))
        {
            return WinPeResult<string>.Success(_sevenZipExecutablePath);
        }

        string runtimeFolderName = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        string executablePath = Path.Combine(
            AppContext.BaseDirectory,
            BundledSevenZipRelativePath,
            runtimeFolderName,
            "7za.exe");

        if (!File.Exists(executablePath))
        {
            return WinPeResult<string>.Failure(
                WinPeErrorCodes.ToolNotFound,
                "Bundled 7-Zip executable was not found.",
                $"Expected file: '{executablePath}'. Ensure Assets\\7z is copied to output.");
        }

        return WinPeResult<string>.Success(executablePath);
    }
}
