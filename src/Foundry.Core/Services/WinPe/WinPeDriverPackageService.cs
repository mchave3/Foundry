// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Foundry.Core.Services.Security;
using Foundry.Utilities.IO;
using Foundry.Utilities.Networking;
using Foundry.Utilities.Processes;
using Foundry.Utilities.Security;

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
        cancellationToken.ThrowIfCancellationRequested();
        foreach (WinPeDriverCatalogEntry package in packages)
        {
            try
            {
                ValidatePackagePolicy(package);
            }
            catch (InvalidDataException ex)
            {
                return WinPeResult<WinPePreparedDriverSet>.Failure(
                    WinPeErrorCodes.ValidationFailed, ex.Message,
                    failureKind: WinPeFailureKinds.Validation, failureReason: WinPeFailureReasons.InvalidInput);
            }
        }

        var extractedDirectories = new List<string>(packages.Count);
        var downloadedFiles = new List<string>(packages.Count);

        for (int index = 0; index < packages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WinPeDriverCatalogEntry package = packages[index];
            string fileName = ResolvePackageFileName(package);
            string downloadPath = Path.Combine(downloadRootPath, fileName);

            WinPeResult downloadResult = await DownloadPackageAsync(
                package,
                downloadPath,
                index + 1,
                packages.Count,
                downloadProgress,
                cancellationToken).ConfigureAwait(false);

            if (!downloadResult.IsSuccess)
            {
                return WinPeResult<WinPePreparedDriverSet>.Failure(downloadResult.Error!);
            }

            downloadedFiles.Add(downloadPath);

            string normalizedFolderName = $"{index + 1:D2}_{PathSegment.Sanitize(package.Vendor.ToString())}_{PathSegment.Sanitize(package.Id)}";
            string extractPath = Path.Combine(extractRootPath, normalizedFolderName);
            WinPeResult extractionResult;
            try
            {
                using NativeFileLease packageLock = NativeFileLease.OpenRead(downloadPath);
                await ValidatePackageFileAsync(package, downloadPath, cancellationToken).ConfigureAwait(false);
                DirectoryOperations.Recreate(extractPath);
                extractionResult = await ExtractPackageAsync(downloadPath, extractPath, packageLock, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or TimeoutException)
            {
                return WinPeResult<WinPePreparedDriverSet>.Failure(CreateAcquisitionFailure(ex));
            }

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
        var client = new HttpClient(new ValidatedRedirectHandler(
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
            static uri =>
            {
                if (uri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidDataException("Driver package downloads require HTTPS.");
                }
            }))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Foundry/1.0");
        return client;
    }

    private static string ResolvePackageFileName(WinPeDriverCatalogEntry package)
    {
        if (!string.IsNullOrWhiteSpace(package.FileName))
        {
            return package.FileName;
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
        WinPeDriverCatalogEntry package,
        string destinationPath,
        int packageNumber,
        int packageCount,
        IProgress<WinPeDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            string status = BuildDriverDownloadStatus(destinationPath, packageNumber, packageCount);
            ReportDownloadProgress(progress, 0, status);
            FileDigest? digest = string.IsNullOrEmpty(package.Sha256) ? null : new(HashAlgorithmName.SHA256, package.Sha256);
            if (digest is not null && File.Exists(destinationPath))
            {
                await using FileStream cacheLock = new(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string cachedHash = await FileHash.ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
                if (cachedHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    ReportDownloadProgress(progress, 100, "Using verified cached driver package.");
                    return WinPeResult.Success();
                }
            }

            long bytes = await HttpRetry.ExecuteAsync(
                token => ValidatedFileTransfer.DownloadAsync(_httpClient, new Uri(package.DownloadUri), destinationPath,
                    new FileIntegrity(digest, null), new TransferLimits(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(2)),
                    digest is null ? (path, stagedToken) => VerifySignatureAsync(package, path, stagedToken) : null,
                    new InlineProgress<long>(count => ReportDownloadProgress(progress, null, $"{status} ({FormatBytes(count)} downloaded)")), token),
                new HttpRetryOptions(3, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)),
                cancellationToken).ConfigureAwait(false);
            ReportDownloadProgress(progress, 100, $"{status} ({FormatBytes(bytes)} downloaded)");
            return WinPeResult.Success();
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return WinPeResult.Failure(CreateAcquisitionFailure(ex));
        }
    }

    private static void ValidatePackagePolicy(WinPeDriverCatalogEntry package)
    {
        string fileName = ResolvePackageFileName(package);
        if (fileName is "." or ".." || Path.GetFileName(fileName) != fileName ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.EndsWith('.') || fileName.EndsWith(' '))
        {
            throw new InvalidDataException("Driver package filename must be a contained filename.");
        }

        if (!Uri.TryCreate(package.DownloadUri, UriKind.Absolute, out Uri? source) || source.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Driver package downloads require an absolute HTTPS source.");
        }

        if (!string.IsNullOrEmpty(package.Sha256))
        {
            if (package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("The supplied driver package SHA256 digest is malformed.");
            }
            return;
        }

        if (!Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Driver package integrity is unavailable: archives require an explicit trusted SHA256 digest.");
        }

        string family = GetPackageFamily(package);
        VendorExecutableTrustPolicy.GetExpectedPublisherSubjects(family);
        VendorExecutableTrustPolicy.ValidateDownloadSource(family, source);
        if (package.CatalogRevision.Length != 64 || !package.CatalogRevision.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(package.Id) || !string.Equals(Path.GetFileName(source.LocalPath), fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signature-only driver acquisition requires authenticated catalog identity and an exact source filename.");
        }
    }

    private static async Task ValidatePackageFileAsync(WinPeDriverCatalogEntry package, string path, CancellationToken token)
    {
        if (string.IsNullOrEmpty(package.Sha256))
        {
            await VerifySignatureAsync(package, path, token).ConfigureAwait(false);
            return;
        }

        string actual = await FileHash.ComputeSha256Async(path, token).ConfigureAwait(false);
        if (!actual.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The driver package failed SHA256 verification before extraction.");
        }
    }

    private static async Task VerifySignatureAsync(WinPeDriverCatalogEntry package, string path, CancellationToken token)
    {
        await using FileStream packageLock = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await AuthenticodeVerifier.VerifyAsync(path, VendorExecutableTrustPolicy.GetExpectedPublisherSubjects(GetPackageFamily(package)), token).ConfigureAwait(false);
    }

    private static string GetPackageFamily(WinPeDriverCatalogEntry package) => package.Vendor switch
    {
        WinPeVendorSelection.Dell => "DellDriverPack",
        WinPeVendorSelection.Lenovo => "LenovoDriverPack",
        WinPeVendorSelection.Microsoft => "SurfaceDriverPack",
        WinPeVendorSelection.Hp => "HpWinPeDriverPack",
        WinPeVendorSelection.Intel => "IntelDriverPack",
        _ => "UnknownDriverPack"
    };

    internal static WinPeDiagnostic CreateAcquisitionFailure(Exception error)
    {
        (string kind, string reason) = error switch
        {
            InvalidDataException => (WinPeFailureKinds.Validation, WinPeFailureReasons.InvalidInput),
            TimeoutException or OperationCanceledException => (WinPeFailureKinds.Network, WinPeFailureReasons.Timeout),
            UnauthorizedAccessException => (WinPeFailureKinds.FileSystem, WinPeFailureReasons.AccessDenied),
            HttpRequestException { StatusCode: not null } => (WinPeFailureKinds.Network, WinPeFailureReasons.HttpStatus),
            HttpRequestException or TransferReadException => (WinPeFailureKinds.Network, WinPeFailureReasons.Transport),
            _ => (WinPeFailureKinds.FileSystem, WinPeFailureReasons.IoError)
        };
        string summary = error.Message;
        if (error is HttpRequestException { StatusCode: { } status })
        {
            using var response = new HttpResponseMessage(status);
            summary = $"HTTP {(int)status} {response.ReasonPhrase}";
        }
        return new WinPeDiagnostic(error is InvalidDataException ? WinPeErrorCodes.HashMismatch : WinPeErrorCodes.DownloadFailed,
            "Failed to acquire a verified package.", failureKind: kind, failureReason: reason, errorSummary: summary, exception: error);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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

    private async Task<WinPeResult> ExtractPackageAsync(
        string packagePath,
        string destinationPath,
        NativeFileLease packageLease,
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

        WinPeProcessExecution extractionResult = await packageLease.RunAsync(() => _processRunner.RunAsync(
            sevenZipExecutablePathResult.Value!,
            ["x", "-y", $"-o{destinationPath}", packagePath],
            destinationPath,
            cancellationToken), cancellationToken).ConfigureAwait(false);

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
