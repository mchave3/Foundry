// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Foundry.Utilities.IO;
using Foundry.Utilities.Networking;
using Foundry.Utilities.Processes;

namespace Foundry.Core.Services.WinPe;

public sealed partial class WinReBootImagePreparationService : IWinReBootImagePreparationService
{
    public static readonly Uri DefaultOperatingSystemCatalogUri =
        new("https://raw.githubusercontent.com/foundry-osd/catalog/refs/heads/main/Cache/OS/OperatingSystem.xml");

    private static readonly string[] RequiredWirelessDependencyFiles =
    [
        "dmcmnutils.dll",
        "mdmregistration.dll"
    ];

    private readonly IWinPeProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _catalogHttpClient;

    public WinReBootImagePreparationService()
        : this(new WinPeProcessRunner(), CreateHttpClient(allowWindowsUpdateHttp: true), CreateHttpClient(allowWindowsUpdateHttp: false))
    {
    }

    internal WinReBootImagePreparationService(IWinPeProcessRunner processRunner, HttpClient httpClient, HttpClient? catalogHttpClient = null)
    {
        _processRunner = processRunner;
        _httpClient = httpClient;
        _catalogHttpClient = catalogHttpClient ?? httpClient;
    }

    public async Task<WinPeResult<WinReBootImagePreparationResult>> ReplaceBootWimAsync(
        WinReBootImagePreparationOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WinPeDiagnostic? validationError = ValidateOptions(options);
        if (validationError is not null)
        {
            return WinPeResult<WinReBootImagePreparationResult>.Failure(validationError);
        }

        ReportProgress(options.Progress, 2, "Resolving WinRE source catalog.");
        WinPeResult<IReadOnlyList<WinReSourceCandidate>> candidatesResult = await SelectCatalogCandidatesAsync(
            options.CatalogUri,
            options.Artifact.Architecture,
            options.WinPeLanguage,
            cancellationToken).ConfigureAwait(false);

        if (!candidatesResult.IsSuccess)
        {
            return WinPeResult<WinReBootImagePreparationResult>.Failure(candidatesResult.Error!);
        }

        ReportProgress(options.Progress, 4, "Selected WinRE source package.");
        var failures = new List<WinPeDiagnostic>();
        foreach (WinReSourceCandidate candidate in candidatesResult.Value!)
        {
            WinPeResult<WinReBootImagePreparationResult> result = await TryReplaceBootWimFromSourceAsync(
                options,
                candidate,
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return result;
            }

            if (result.Error?.Exception is { } error && NativeFileLease.HasRetainedProtection(error))
            {
                return result;
            }

            failures.Add(result.Error!);
        }

        return WinPeResult<WinReBootImagePreparationResult>.Failure(
            WinPeErrorCodes.WinReExtractionFailed,
            "Failed to prepare a WinRE Wi-Fi boot image from every matching operating system source.",
            string.Join(Environment.NewLine + Environment.NewLine, failures.Select(failure => failure.Details ?? failure.Message)));
    }

    internal static WinPeResult<IReadOnlyList<WinReSourceCandidate>> SelectCatalogCandidates(
        string catalogXml,
        WinPeArchitecture architecture,
        string languageCode)
    {
        if (string.IsNullOrWhiteSpace(catalogXml))
        {
            return WinPeResult<IReadOnlyList<WinReSourceCandidate>>.Failure(
                WinPeErrorCodes.OperatingSystemCatalogParseFailed,
                "The operating system catalog is empty.");
        }

        try
        {
            string normalizedArchitecture = NormalizeArchitecture(architecture);
            string normalizedLanguage = WinPeLanguageUtility.Normalize(languageCode);
            XDocument document = XDocument.Parse(catalogXml);

            List<WinReCatalogItem> matchingItems = document.Descendants("Item")
                .Select(ParseCatalogItem)
                .Where(item =>
                    item.WindowsRelease.Equals("11", StringComparison.OrdinalIgnoreCase) &&
                    item.ReleaseId.Equals("24H2", StringComparison.OrdinalIgnoreCase) &&
                    item.Architecture.Equals(normalizedArchitecture, StringComparison.OrdinalIgnoreCase) &&
                    WinPeLanguageUtility.Normalize(item.LanguageCode).Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.Url))
                .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(item => GetLicenseChannelOrder(item.LicenseChannel))
                    .ThenBy(item => GetClientTypeOrder(item.ClientType))
                    .ThenByDescending(item => item.BuildMajor)
                    .ThenByDescending(item => item.BuildUbr)
                    .First())
                .ToList();

            WinReCatalogItem? proSource = SelectPreferredSourceItem(matchingItems, "CLIENTCONSUMER");
            WinReCatalogItem? enterpriseSource = SelectPreferredSourceItem(matchingItems, "CLIENTBUSINESS");

            var candidates = new List<WinReSourceCandidate>(2);
            if (proSource is not null)
            {
                candidates.Add(new WinReSourceCandidate
                {
                    RequestedEdition = "Pro",
                    Source = proSource
                });
            }

            if (enterpriseSource is not null)
            {
                candidates.Add(new WinReSourceCandidate
                {
                    RequestedEdition = "Enterprise",
                    Source = enterpriseSource
                });
            }

            if (candidates.Count == 0)
            {
                return WinPeResult<IReadOnlyList<WinReSourceCandidate>>.Failure(
                    WinPeErrorCodes.WinReSourceSelectionFailed,
                    "No Windows 11 24H2 WinRE source matched the requested architecture and language.",
                    $"Architecture={normalizedArchitecture}, Language={normalizedLanguage}");
            }

            return WinPeResult<IReadOnlyList<WinReSourceCandidate>>.Success(candidates);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Xml.XmlException)
        {
            return WinPeResult<IReadOnlyList<WinReSourceCandidate>>.Failure(
                WinPeErrorCodes.OperatingSystemCatalogParseFailed,
                "Failed to parse the operating system catalog.",
                ex.Message);
        }
    }

    internal static async Task<WinPeResult> ValidateHashIfRequestedAsync(
        string filePath,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(expectedHash) || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            return WinPeResult.Failure(WinPeErrorCodes.ValidationFailed, "WinRE source integrity requires an explicit valid SHA256 digest.");
        }

        string actualHash = await FileHash.ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
        if (expectedHash.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
        {
            return WinPeResult.Success();
        }

        return WinPeResult.Failure(
            WinPeErrorCodes.HashMismatch,
            "The cached WinRE source package failed hash validation.",
            $"Expected SHA256={expectedHash}; Actual SHA256={actualHash}.");
    }

    internal static WinPeResult<int> ResolveImageIndexFromOutput(string output, string requestedEdition)
    {
        string normalizedRequestedEdition = NormalizeToken(requestedEdition);
        if (normalizedRequestedEdition.Length == 0)
        {
            return WinPeResult<int>.Failure(
                WinPeErrorCodes.ValidationFailed,
                "Requested Windows edition is required.");
        }

        ImageIndexDescriptor? match = ParseImageDescriptors(output)
            .FirstOrDefault(descriptor =>
                ContainsNormalized(descriptor.Name, normalizedRequestedEdition) ||
                ContainsNormalized(descriptor.Edition, normalizedRequestedEdition) ||
                ContainsNormalized(descriptor.EditionId, normalizedRequestedEdition));

        if (match is null)
        {
            return WinPeResult<int>.Failure(
                WinPeErrorCodes.WinReIndexResolutionFailed,
                $"Could not resolve a Windows image index for edition '{requestedEdition}'.",
                output);
        }

        return WinPeResult<int>.Success(match.Index);
    }

    internal static WinPeResult<WinReBootImagePreparationResult> PrepareWirelessDependencyFiles(
        string mountedImagePath,
        string dependencyDirectoryPath)
    {
        string sourceSystem32Path = Path.Combine(mountedImagePath, "Windows", "System32");

        try
        {
            Directory.CreateDirectory(dependencyDirectoryPath);

            var dependencyFiles = new List<WinReDependencyFile>(RequiredWirelessDependencyFiles.Length);
            foreach (string fileName in RequiredWirelessDependencyFiles)
            {
                string sourcePath = Path.Combine(sourceSystem32Path, fileName);
                if (!File.Exists(sourcePath))
                {
                    return WinPeResult<WinReBootImagePreparationResult>.Failure(
                        WinPeErrorCodes.WinReExtractionFailed,
                        $"The selected operating system image is missing the required wireless dependency '{fileName}'.",
                        $"Expected path: '{sourcePath}'.");
                }

                string stagedPath = Path.Combine(dependencyDirectoryPath, fileName);
                File.Copy(sourcePath, stagedPath, overwrite: true);
                dependencyFiles.Add(new WinReDependencyFile
                {
                    FileName = fileName,
                    StagedPath = stagedPath
                });
            }

            return WinPeResult<WinReBootImagePreparationResult>.Success(new WinReBootImagePreparationResult
            {
                DependencyFiles = dependencyFiles
            });
        }
        catch (Exception ex)
        {
            return WinPeResult<WinReBootImagePreparationResult>.Failure(
                WinPeErrorCodes.WinReExtractionFailed,
                "Failed to stage required wireless dependency files from the mounted operating system image.",
                ex.Message);
        }
    }

    private async Task<WinPeResult<IReadOnlyList<WinReSourceCandidate>>> SelectCatalogCandidatesAsync(
        Uri catalogUri,
        WinPeArchitecture architecture,
        string languageCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!catalogUri.IsAbsoluteUri || catalogUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Operating system catalog requests require HTTPS.");
            }
            string catalogXml = await HttpRetry.ExecuteAsync(async token =>
            {
                using HttpResponseMessage response = await _catalogHttpClient.GetAsync(catalogUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
                {
                    throw new InvalidDataException("Operating system catalog redirects require HTTPS.");
                }
                return await BoundedHttpContent.ReadStringAsync(response, 32 * 1024 * 1024, token).ConfigureAwait(false);
            }, new HttpRetryOptions(3, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
            return SelectCatalogCandidates(catalogXml, architecture, languageCode);
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or InvalidOperationException or TimeoutException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return WinPeResult<IReadOnlyList<WinReSourceCandidate>>.Failure(
                WinPeErrorCodes.OperatingSystemCatalogFetchFailed,
                "Failed to download the operating system catalog.",
                failureKind: WinPeDriverPackageService.CreateAcquisitionFailure(ex).FailureKind,
                failureReason: WinPeDriverPackageService.CreateAcquisitionFailure(ex).FailureReason,
                exception: ex);
        }
    }

    private async Task<WinPeResult<WinReBootImagePreparationResult>> TryReplaceBootWimFromSourceAsync(
        WinReBootImagePreparationOptions options,
        WinReSourceCandidate candidate,
        CancellationToken cancellationToken)
    {
        string candidateName = PathSegment.Sanitize(candidate.RequestedEdition);
        string sourceDirectory = Path.Combine(options.Artifact.WorkingDirectoryPath, $"winre-source-{candidateName}");
        string exportDirectory = Path.Combine(sourceDirectory, "export");
        string mountDirectory = Path.Combine(sourceDirectory, "install-mount");
        string dependencyDirectory = Path.Combine(sourceDirectory, "wireless-support");
        string installWimPath = Path.Combine(exportDirectory, "install.wim");

        WinPeMountSession? session = null;
        try
        {
            ReportProgress(options.Progress, 5, "Preparing WinRE source package.");

            WinPeResult<string> sourcePathResult = await EnsureDownloadedAsync(
                options.CacheDirectoryPath,
                candidate.Source,
                options.DownloadProgress,
                cancellationToken).ConfigureAwait(false);

            if (!sourcePathResult.IsSuccess)
            {
                return WinPeResult<WinReBootImagePreparationResult>.Failure(sourcePathResult.Error!);
            }

            using NativeFileLease sourceLock = NativeFileLease.OpenRead(sourcePathResult.Value!);
            WinPeResult sourceIntegrity = await ValidateHashIfRequestedAsync(sourcePathResult.Value!, candidate.Source.Sha256, cancellationToken).ConfigureAwait(false);
            if (!sourceIntegrity.IsSuccess)
            {
                return WinPeResult<WinReBootImagePreparationResult>.Failure(sourceIntegrity.Error!);
            }
            DirectoryOperations.Recreate(sourceDirectory);
            Directory.CreateDirectory(exportDirectory);

            ReportProgress(options.Progress, 16, "Resolving WinRE image index.");
            WinPeResult<int> indexResult = await ResolveImageIndexAsync(
                options.Tools.DismPath,
                sourcePathResult.Value!,
                candidate.RequestedEdition,
                options.Artifact.WorkingDirectoryPath,
                sourceLock,
                CreateDismProgress(options.Progress, 16, "Resolving WinRE image index."),
                cancellationToken).ConfigureAwait(false);

            if (!indexResult.IsSuccess)
            {
                return WinPeResult<WinReBootImagePreparationResult>.Failure(indexResult.Error!);
            }

            ReportProgress(options.Progress, 19, "Exporting Windows image for WinRE extraction.");
            WinPeProcessExecution exportResult = await sourceLock.RunAsync(() => WinPeDismProcessRunner.RunAsync(
                _processRunner,
                options.Tools.DismPath,
                ["/Export-Image", $"/SourceImageFile:{sourcePathResult.Value!}", $"/SourceIndex:{indexResult.Value}", $"/DestinationImageFile:{installWimPath}", "/Compress:max", "/CheckIntegrity"],
                options.Artifact.WorkingDirectoryPath,
                "Exporting Windows image with DISM.",
                CreateDismProgress(options.Progress, 19, "Exporting Windows image for WinRE extraction."),
                cancellationToken), cancellationToken).ConfigureAwait(false);

            if (!exportResult.IsSuccess || !File.Exists(installWimPath))
            {
                return WinPeResult<WinReBootImagePreparationResult>.Failure(
                    WinPeErrorCodes.WinReExtractionFailed,
                    $"Failed to export the {candidate.RequestedEdition} image from the WinRE source package.",
                    exportResult.ToDiagnosticText());
            }

            ReportProgress(options.Progress, 24, "Mounting WinRE source image.");
            WinPeResult<WinPeMountSession> mountResult = await WinPeMountSession.MountAsync(
                _processRunner,
                options.Tools.DismPath,
                installWimPath,
                mountDirectory,
                options.Artifact.WorkingDirectoryPath,
                cancellationToken,
                CreateDismProgress(options.Progress, 24, "Mounting WinRE source image.")).ConfigureAwait(false);

            if (!mountResult.IsSuccess)
            {
                return WinPeResult<WinReBootImagePreparationResult>.Failure(mountResult.Error!);
            }

            session = mountResult.Value!;
            string winRePath = Path.Combine(mountDirectory, "Windows", "System32", "Recovery", "winre.wim");
            if (!File.Exists(winRePath))
            {
                return await FailWithDiscardAsync(
                    new WinPeDiagnostic(
                        WinPeErrorCodes.WinReExtractionFailed,
                        "The selected operating system image does not contain winre.wim.",
                        $"Expected path: '{winRePath}'."),
                    session,
                    cancellationToken).ConfigureAwait(false);
            }

            ReportProgress(options.Progress, 27, "Staging WinRE Wi-Fi dependencies.");
            WinPeResult<WinReBootImagePreparationResult> dependencyResult = PrepareWirelessDependencyFiles(
                mountDirectory,
                dependencyDirectory);

            if (!dependencyResult.IsSuccess)
            {
                return await FailWithDiscardAsync(dependencyResult.Error!, session, cancellationToken).ConfigureAwait(false);
            }

            ReportProgress(options.Progress, 29, "Replacing boot image with WinRE.");
            Directory.CreateDirectory(Path.GetDirectoryName(options.Artifact.BootWimPath)!);
            File.Copy(winRePath, options.Artifact.BootWimPath, overwrite: true);

            WinPeResult discardResult = await session.DiscardAsync(cancellationToken).ConfigureAwait(false);
            session = null;
            if (!discardResult.IsSuccess)
            {
                return WinPeResult<WinReBootImagePreparationResult>.Failure(discardResult.Error!);
            }

            TryDeleteDirectory(exportDirectory);
            TryDeleteDirectory(mountDirectory);
            ReportProgress(options.Progress, 30, "WinRE Wi-Fi boot image is ready.");
            return dependencyResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (session is not null)
            {
                return await FailWithDiscardAsync(
                    new WinPeDiagnostic(
                        WinPeErrorCodes.WinReExtractionFailed,
                        "Failed to replace boot.wim with a WinRE Wi-Fi source image.",
                        ex.Message,
                        exception: ex),
                    session,
                    cancellationToken).ConfigureAwait(false);
            }

            return WinPeResult<WinReBootImagePreparationResult>.Failure(
                WinPeErrorCodes.WinReExtractionFailed,
                "Failed to replace boot.wim with a WinRE Wi-Fi source image.",
                ex.Message,
                exception: ex);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal async Task<WinPeResult<string>> EnsureDownloadedAsync(
        string cacheDirectoryPath,
        WinReCatalogItem source,
        IProgress<WinPeDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(source.Sha256) || source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit))
        {
            return WinPeResult<string>.Failure(WinPeErrorCodes.ValidationFailed, "WinRE source integrity requires an explicit valid SHA256 digest.");
        }
        string sourceCachePath = BuildCachedSourcePath(cacheDirectoryPath, source);
        if (Path.GetFileName(sourceCachePath) is "." or ".." ||
            (!string.IsNullOrEmpty(source.FileName) && (Path.GetFileName(source.FileName) != source.FileName ||
                source.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || source.FileName.EndsWith('.') || source.FileName.EndsWith(' '))))
        {
            return WinPeResult<string>.Failure(WinPeErrorCodes.ValidationFailed, "WinRE source filename must be a contained filename.");
        }
        if (!Uri.TryCreate(WindowsUpdateContentUrl.Normalize(source.Url), UriKind.Absolute, out Uri? sourceUri))
        {
            return WinPeResult<string>.Failure(WinPeErrorCodes.ValidationFailed, "The WinRE source URL is invalid.");
        }

        try
        {
            ValidateSourceUri(sourceUri, allowWindowsUpdateHttp: true);
            if (File.Exists(sourceCachePath))
            {
                await using FileStream cacheLock = new(sourceCachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                WinPeResult cachedHash = await ValidateHashIfRequestedAsync(sourceCachePath, source.Sha256, cancellationToken).ConfigureAwait(false);
                if (cachedHash.IsSuccess)
                {
                    ReportDownloadProgress(progress, 100, "Using verified cached WinRE source package.");
                    return WinPeResult<string>.Success(sourceCachePath);
                }
            }

            ReportDownloadProgress(progress, 0, "Downloading WinRE source package.");
            long bytes = await HttpRetry.ExecuteAsync(token => ValidatedFileTransfer.DownloadAsync(
                _httpClient, sourceUri, sourceCachePath, new FileIntegrity(new FileDigest(HashAlgorithmName.SHA256, source.Sha256), null),
                new TransferLimits(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(2)),
                progress: new DownloadProgress(progress), cancellationToken: token),
                new HttpRetryOptions(3, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)),
                cancellationToken).ConfigureAwait(false);
            ReportDownloadProgress(progress, 100, $"Downloaded WinRE source package ({FormatBytes(bytes)}).");
            return WinPeResult<string>.Success(sourceCachePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return WinPeResult<string>.Failure(WinPeDriverPackageService.CreateAcquisitionFailure(ex));
        }
    }

    private static HttpClient CreateHttpClient(bool allowWindowsUpdateHttp) => new(new ValidatedRedirectHandler(
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
        uri => ValidateSourceUri(uri, allowWindowsUpdateHttp))) { Timeout = TimeSpan.FromSeconds(30) };

    private static void ValidateSourceUri(Uri source, bool allowWindowsUpdateHttp)
    {
        bool microsoftContent = source.Host.Equals("dl.delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            source.Host.EndsWith(".dl.delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase);
        if (source.Scheme != Uri.UriSchemeHttps &&
            !(allowWindowsUpdateHttp && source.Scheme == Uri.UriSchemeHttp && microsoftContent && source.IsDefaultPort))
        {
            throw new InvalidDataException("WinRE requests require HTTPS except authenticated-digest Microsoft ESD delivery.");
        }
    }

    private sealed class DownloadProgress(IProgress<WinPeDownloadProgress>? progress) : IProgress<long>
    {
        public void Report(long bytes) => ReportDownloadProgress(progress, null, $"Downloading WinRE source package ({FormatBytes(bytes)} downloaded).");
    }
    private async Task<WinPeResult<int>> ResolveImageIndexAsync(
        string dismPath,
        string sourceImagePath,
        string requestedEdition,
        string workingDirectory,
        NativeFileLease sourceLease,
        IProgress<WinPeDismProgress>? dismProgress,
        CancellationToken cancellationToken)
    {
        WinPeProcessExecution imageInfoResult = await sourceLease.RunAsync(() => WinPeDismProcessRunner.RunAsync(
            _processRunner,
            dismPath,
            ["/English", "/Get-ImageInfo", $"/ImageFile:{sourceImagePath}"],
            workingDirectory,
            "Resolving WinRE image index with DISM.",
            dismProgress,
            cancellationToken,
            executionTimeout: TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);

        if (!imageInfoResult.IsSuccess)
        {
            return WinPeResult<int>.Failure(
                WinPeErrorCodes.WinReIndexResolutionFailed,
                "Failed to inspect the WinRE source package image indexes.",
                imageInfoResult.ToDiagnosticText());
        }

        imageInfoResult.EnsureCompleteOutput();
        return ResolveImageIndexFromOutput(imageInfoResult.StandardOutput, requestedEdition);
    }

    private static WinPeDiagnostic? ValidateOptions(WinReBootImagePreparationOptions? options)
    {
        if (options is null)
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "WinRE boot image preparation options are required.");
        }

        if (string.IsNullOrWhiteSpace(options.Artifact.WorkingDirectoryPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "WinPE working directory path is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Artifact.BootWimPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "WinPE boot.wim path is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Tools.DismPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "DISM path is required.");
        }

        if (string.IsNullOrWhiteSpace(options.CacheDirectoryPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "WinRE source cache directory path is required.");
        }

        return null;
    }

    private static string BuildCachedSourcePath(string cacheDirectoryPath, WinReCatalogItem source)
    {
        string fileName = string.IsNullOrWhiteSpace(source.FileName)
            ? $"{source.ReleaseId}-{source.Architecture}-{source.LanguageCode}.esd"
            : source.FileName;

        return Path.Combine(
            cacheDirectoryPath,
            PathSegment.Sanitize(fileName));
    }

    private static async Task<WinPeResult<WinReBootImagePreparationResult>> FailWithDiscardAsync(
        WinPeDiagnostic primaryDiagnostic,
        WinPeMountSession session,
        CancellationToken cancellationToken)
    {
        WinPeResult discardResult = await session.DiscardAsync(cancellationToken).ConfigureAwait(false);
        if (discardResult.IsSuccess)
        {
            return WinPeResult<WinReBootImagePreparationResult>.Failure(primaryDiagnostic);
        }

        string details = string.Join(
            Environment.NewLine,
            primaryDiagnostic.Details ?? string.Empty,
            "Discard diagnostics:",
            discardResult.Error?.Details ?? string.Empty).Trim();

        return WinPeResult<WinReBootImagePreparationResult>.Failure(new WinPeDiagnostic(
            primaryDiagnostic.Code,
            primaryDiagnostic.Message,
            details));
    }

    private static WinReCatalogItem ParseCatalogItem(XElement item)
    {
        return new WinReCatalogItem
        {
            WindowsRelease = ReadElement(item, "WindowsRelease"),
            ReleaseId = ReadElement(item, "ReleaseId"),
            BuildMajor = ParseInt(ReadElement(item, "BuildMajor")),
            BuildUbr = ParseInt(ReadElement(item, "BuildUbr")),
            Architecture = NormalizeArchitecture(ReadElement(item, "Architecture")),
            LanguageCode = ReadElement(item, "LanguageCode"),
            Edition = ReadElement(item, "Edition"),
            ClientType = ReadElement(item, "ClientType"),
            LicenseChannel = ReadElement(item, "LicenseChannel"),
            FileName = ReadElement(item, "FileName"),
            Url = ReadElement(item, "Url"),
            Sha256 = ReadElement(item, "Sha256")
        };
    }

    private static void ReportProgress(
        IProgress<WinPeMountedImageCustomizationProgress>? progress,
        int percent,
        string status)
    {
        progress?.Report(new WinPeMountedImageCustomizationProgress
        {
            Percent = Math.Clamp(percent, 0, 100),
            Status = status
        });
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

    private static IProgress<WinPeDismProgress>? CreateDismProgress(
        IProgress<WinPeMountedImageCustomizationProgress>? progress,
        int percent,
        string status)
    {
        return progress is null ? null : new WinPeDismProgressForwarder(progress, percent, status);
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

    private static string ReadElement(XElement parent, string elementName)
    {
        return (parent.Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty).Trim();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    private static string NormalizeArchitecture(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "amd64" => "x64",
            "aarch64" => "arm64",
            _ => normalized
        };
    }

    private static string NormalizeArchitecture(WinPeArchitecture architecture)
    {
        return architecture switch
        {
            WinPeArchitecture.X64 => "x64",
            WinPeArchitecture.Arm64 => "arm64",
            _ => architecture.ToString().ToLowerInvariant()
        };
    }

    private static WinReCatalogItem? SelectPreferredSourceItem(
        IEnumerable<WinReCatalogItem> items,
        string clientType)
    {
        return items
            .Where(item => item.ClientType.Equals(clientType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => GetLicenseChannelOrder(item.LicenseChannel))
            .ThenBy(item => GetClientTypeOrder(item.ClientType))
            .ThenByDescending(item => item.BuildMajor)
            .ThenByDescending(item => item.BuildUbr)
            .FirstOrDefault();
    }

    private static int GetLicenseChannelOrder(string licenseChannel)
    {
        return licenseChannel.Equals("RET", StringComparison.OrdinalIgnoreCase)
            ? 0
            : licenseChannel.Equals("VOL", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 99;
    }

    private static int GetClientTypeOrder(string clientType)
    {
        return clientType.Equals("CLIENTCONSUMER", StringComparison.OrdinalIgnoreCase)
            ? 0
            : clientType.Equals("CLIENTBUSINESS", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 99;
    }

    private static IReadOnlyList<ImageIndexDescriptor> ParseImageDescriptors(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var descriptors = new List<ImageIndexDescriptor>();
        ImageIndexDescriptor? current = null;

        foreach (string line in output.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            Match indexMatch = Regex.Match(line, @"^\s*Index\s*:\s*(\d+)\s*$", RegexOptions.IgnoreCase);
            if (indexMatch.Success)
            {
                if (current is not null)
                {
                    descriptors.Add(current);
                }

                current = new ImageIndexDescriptor
                {
                    Index = int.Parse(indexMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                    Name = string.Empty,
                    Edition = string.Empty,
                    EditionId = string.Empty
                };

                continue;
            }

            if (current is null)
            {
                continue;
            }

            Match nameMatch = Regex.Match(line, @"^\s*Name\s*:\s*(.+)\s*$", RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                current = current with { Name = nameMatch.Groups[1].Value.Trim() };
                continue;
            }

            Match editionMatch = Regex.Match(line, @"^\s*Edition\s*:\s*(.+)\s*$", RegexOptions.IgnoreCase);
            if (editionMatch.Success)
            {
                current = current with { Edition = editionMatch.Groups[1].Value.Trim() };
                continue;
            }

            Match editionIdMatch = Regex.Match(line, @"^\s*Edition\s+ID\s*:\s*(.+)\s*$", RegexOptions.IgnoreCase);
            if (editionIdMatch.Success)
            {
                current = current with { EditionId = editionIdMatch.Groups[1].Value.Trim() };
            }
        }

        if (current is not null)
        {
            descriptors.Add(current);
        }

        return descriptors;
    }

    private static bool ContainsNormalized(string source, string expected)
    {
        string normalized = NormalizeToken(source);
        if (normalized.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        return normalized.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
               expected.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        char[] filtered = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(filtered);
    }

    private sealed record ImageIndexDescriptor
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Edition { get; init; }
        public required string EditionId { get; init; }
    }
}
