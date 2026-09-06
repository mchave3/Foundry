// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Linq;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.IO;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.DriverPacks;

public sealed class DriverPackExtractionService : IDriverPackExtractionService
{
    private readonly IArchiveExtractionService _archiveExtractionService;
    private readonly IMicrosoftUpdateCatalogDriverService _microsoftUpdateCatalogDriverService;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<DriverPackExtractionService> _logger;

    public DriverPackExtractionService(
        IArchiveExtractionService archiveExtractionService,
        IMicrosoftUpdateCatalogDriverService microsoftUpdateCatalogDriverService,
        IProcessRunner processRunner,
        ILogger<DriverPackExtractionService> logger)
    {
        _archiveExtractionService = archiveExtractionService;
        _microsoftUpdateCatalogDriverService = microsoftUpdateCatalogDriverService;
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<DriverPackExtractionResult> ExtractAsync(
        DriverPackExecutionPlan executionPlan,
        string extractionRootPath,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);

        Directory.CreateDirectory(extractionRootPath);
        progress?.Report(0d);

        if (executionPlan.InstallMode == DriverPackInstallMode.None)
        {
            progress?.Report(100d);
            return new DriverPackExtractionResult
            {
                ExecutionPlan = executionPlan,
                ExtractedDirectoryPath = null,
                InfCount = 0,
                Message = "Driver pack extraction skipped."
            };
        }

        if (executionPlan.InstallMode == DriverPackInstallMode.DeferredSetupComplete)
        {
            progress?.Report(25d);
            progress?.Report(75d);
            progress?.Report(100d);
            return new DriverPackExtractionResult
            {
                ExecutionPlan = executionPlan,
                ExtractedDirectoryPath = null,
                InfCount = 0,
                Message = "Driver pack does not require WinPE extraction; deferred installation will be staged."
            };
        }

        string packageFolderName = executionPlan.ExtractionMethod == DriverPackExtractionMethod.MicrosoftUpdateCatalogExpand
            ? "MicrosoftUpdateCatalog"
            : SanitizePathSegment(Path.GetFileNameWithoutExtension(executionPlan.DownloadedPath));
        string extractedPath = Path.Combine(extractionRootPath, packageFolderName);
        DirectoryOperations.Recreate(extractedPath);

        _logger.LogInformation(
            "Extracting driver pack. InstallMode={InstallMode}, ExtractionMethod={ExtractionMethod}, DownloadedPath={DownloadedPath}, ExtractedPath={ExtractedPath}",
            executionPlan.InstallMode,
            executionPlan.ExtractionMethod,
            executionPlan.DownloadedPath,
            extractedPath);

        switch (executionPlan.ExtractionMethod)
        {
            case DriverPackExtractionMethod.SevenZip:
                await ExtractWithSevenZipAsync(executionPlan.DownloadedPath, extractedPath, extractionRootPath, cancellationToken, progress)
                    .ConfigureAwait(false);
                break;

            case DriverPackExtractionMethod.DellSelfExtractor:
                await ExtractDellSelfExtractorAsync(executionPlan.DownloadedPath, extractedPath, extractionRootPath, cancellationToken, progress)
                    .ConfigureAwait(false);
                break;

            case DriverPackExtractionMethod.MicrosoftUpdateCatalogExpand:
            {
                MicrosoftUpdateCatalogDriverResult microsoftResult = await _microsoftUpdateCatalogDriverService
                    .ExpandAsync(executionPlan.DownloadedPath, extractedPath, cancellationToken, progress)
                    .ConfigureAwait(false);

                progress?.Report(100d);
                return new DriverPackExtractionResult
                {
                    ExecutionPlan = executionPlan,
                    ExtractedDirectoryPath = microsoftResult.DestinationDirectory,
                    InfCount = microsoftResult.InfCount,
                    Message = microsoftResult.Message
                };
            }

            default:
                throw new InvalidOperationException(
                    $"Unsupported extraction method '{executionPlan.ExtractionMethod}'.");
        }

        int infCount = Directory
            .EnumerateFiles(extractedPath, "*.inf", SearchOption.AllDirectories)
            .Count();

        if (executionPlan.RequiresInfPayload && infCount == 0)
        {
            throw new InvalidOperationException(
                $"Driver pack extraction completed but no INF files were found in '{extractedPath}'.");
        }

        progress?.Report(100d);

        return new DriverPackExtractionResult
        {
            ExecutionPlan = executionPlan,
            ExtractedDirectoryPath = extractedPath,
            InfCount = infCount,
            Message = infCount > 0
                ? $"Driver pack extracted successfully: {infCount} INF files."
                : "Driver pack extracted successfully."
        };
    }

    private async Task ExtractWithSevenZipAsync(
        string archivePath,
        string extractedPath,
        string workingDirectory,
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        await _archiveExtractionService
            .ExtractWithSevenZipAsync(archivePath, extractedPath, workingDirectory, cancellationToken, progress)
            .ConfigureAwait(false);
    }

    private async Task ExtractDellSelfExtractorAsync(
        string packagePath,
        string extractedPath,
        string workingDirectory,
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        progress?.Report(10d);
        ProcessExecutionResult execution = await _processRunner
            .RunAsync(
                packagePath,
                [
                    "/s",
                    $"/e={extractedPath}"
                ],
                workingDirectory,
                cancellationToken,
                TimeSpan.FromHours(4))
            .ConfigureAwait(false);

        if (!execution.IsSuccess)
        {
            throw new DeploymentProcessException(
                $"Dell driver pack extraction failed for '{packagePath}'.{Environment.NewLine}{execution.ToDiagnosticText()}",
                execution.ExitCode);
        }

        progress?.Report(95d);
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "driverpack";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return sanitized.Trim().TrimEnd('.');
    }

}
