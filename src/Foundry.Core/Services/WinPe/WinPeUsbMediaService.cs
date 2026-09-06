// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Foundry.Utilities.Processes;
using Foundry.Utilities.Serialization;

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeUsbMediaService : IWinPeUsbMediaService
{
    internal const ulong MinimumUsbDiskSizeBytes = 16UL * 1024UL * 1024UL * 1024UL;
    private const string UsbProvisioningProgressPrefix = "FOUNDRY_USB_PROGRESS|";
    private const string UsbProvisioningVerbosePrefix = "FOUNDRY_USB_VERBOSE|";

    private readonly IWinPeProcessRunner _processRunner;
    private readonly IWinPeRuntimePayloadProvisioningService _runtimePayloadProvisioningService;
    private readonly Func<string, string> _resolveVolumeRoot;

    public WinPeUsbMediaService()
        : this(
            new WinPeProcessRunner(),
            new WinPeRuntimePayloadProvisioningService(),
            UseValidatedVolumeRoot)
    {
    }

    internal WinPeUsbMediaService(IWinPeProcessRunner processRunner)
        : this(processRunner, new WinPeRuntimePayloadProvisioningService(processRunner), UseValidatedVolumeRoot)
    {
    }

    internal WinPeUsbMediaService(
        IWinPeProcessRunner processRunner,
        Func<string, string> resolveVolumeRoot)
        : this(processRunner, new WinPeRuntimePayloadProvisioningService(processRunner), resolveVolumeRoot)
    {
    }

    internal WinPeUsbMediaService(
        IWinPeProcessRunner processRunner,
        IWinPeRuntimePayloadProvisioningService runtimePayloadProvisioningService)
        : this(processRunner, runtimePayloadProvisioningService, UseValidatedVolumeRoot)
    {
    }

    internal WinPeUsbMediaService(
        IWinPeProcessRunner processRunner,
        IWinPeRuntimePayloadProvisioningService runtimePayloadProvisioningService,
        Func<string, string> resolveVolumeRoot)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _runtimePayloadProvisioningService = runtimePayloadProvisioningService ??
                                             throw new ArgumentNullException(nameof(runtimePayloadProvisioningService));
        _resolveVolumeRoot = resolveVolumeRoot ?? throw new ArgumentNullException(nameof(resolveVolumeRoot));
    }

    public async Task<WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>>> GetUsbCandidatesAsync(
        WinPeToolPaths tools,
        string workingDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(tools.PowerShellPath))
        {
            return WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>>.Failure(
                WinPeErrorCodes.ValidationFailed,
                "PowerShell path is required to query USB disks.",
                "Set WinPeToolPaths.PowerShellPath.");
        }

        if (string.IsNullOrWhiteSpace(workingDirectoryPath))
        {
            return WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>>.Failure(
                WinPeErrorCodes.ValidationFailed,
                "USB query working directory is required.",
                "Provide a working directory for the USB disk query.");
        }

        Directory.CreateDirectory(workingDirectoryPath);

        string script = ReadUsbDiskOperations() + """
                              $foundryGptBootPartitionType = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'

                              $disks = Get-Disk | Where-Object { $_.BusType -eq 'USB' }
                              $result = @(
                              foreach ($disk in $disks) {
                                  $partitions = @(Get-Partition -Disk $disk -ErrorAction SilentlyContinue)
                                  $volumes = @($partitions | ForEach-Object { try { Get-FoundryUsbPartitionVolume $_ } catch { } })
                                  $letters = @(
                                      $volumes | Where-Object { $_.DriveLetter -ne '' } | ForEach-Object { Get-FoundryUsbDriveLetterText $_.DriveLetter }
                                  )
                                  $hasBootVolume = @($volumes | Where-Object { $_.FileSystemLabel -eq 'BOOT' -and $_.FileSystem -eq 'FAT32' }).Count -gt 0
                                  $hasGptBootPartition = @($partitions | Where-Object { [string]$_.GptType -eq $foundryGptBootPartitionType }).Count -gt 0
                                  $hasMbrBootPartition = @($partitions | Where-Object { [string]$_.MbrType -eq 'FAT32' -and [bool]$_.IsActive }).Count -gt 0
                                  $hasCacheVolume = @($volumes | Where-Object { $_.FileSystemLabel -eq 'Foundry Cache' -and $_.FileSystem -eq 'NTFS' }).Count -gt 0

                                  [pscustomobject]@{
                                      Number = [int]$disk.Number
                                      FriendlyName = [string]$disk.FriendlyName
                                      DriveLetters = ($letters -join ", ")
                                      SerialNumber = [string]$disk.SerialNumber
                                      UniqueId = [string]$disk.UniqueId
                                      BusType = [string]$disk.BusType
                                      IsRemovable = $disk.IsRemovable
                                      IsSystem = [bool]$disk.IsSystem
                                      IsBoot = [bool]$disk.IsBoot
                                      IsOffline = [bool]$disk.IsOffline
                                      IsReadOnly = [bool]$disk.IsReadOnly
                                      Size = [uint64]$disk.Size
                                      IsFoundryMedia = [bool](($hasBootVolume -or $hasGptBootPartition -or $hasMbrBootPartition) -and $hasCacheVolume)
                                  }
                              }
                              )

                              if ($result.Count -eq 0) {
                                  '[]'
                              }
                              else {
                                  $result | ConvertTo-Json -Compress
                              }
                              """;

        WinPeResult<string> result = await RunPowerShellAsync(
            script,
            tools,
            workingDirectoryPath,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>>.Failure(result.Error!);
        }

        try
        {
            IReadOnlyList<WinPeUsbDiskCandidate> candidates = ParseUsbCandidates(result.Value!)
                .Where(candidate =>
                    candidate.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
                    candidate.IsRemovable != false &&
                    !candidate.IsSystem &&
                    !candidate.IsBoot &&
                    !candidate.IsOffline &&
                    !candidate.IsReadOnly)
                .OrderBy(candidate => candidate.DiskNumber)
                .ToArray();

            return WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>>.Success(candidates);
        }
        catch (Exception ex)
        {
            return WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>>.Failure(
                WinPeErrorCodes.UsbQueryFailed,
                "Failed to parse USB disk candidates.",
                ex.Message);
        }
    }

    public Task<WinPeResult<WinPeUsbProvisionResult>> ProvisionAndPopulateAsync(
        UsbOutputOptions options,
        WinPeBuildArtifact artifact,
        WinPeToolPaths tools,
        bool useBootEx,
        CancellationToken cancellationToken = default) =>
        RunUsbFileOperationAsync(() => ProvisionAndPopulateCoreAsync(options, artifact, tools, useBootEx, cancellationToken));

    public Task<WinPeResult<WinPeUsbProvisionResult>> UpdateBootPartitionAsync(
        UsbOutputOptions options,
        WinPeBuildArtifact artifact,
        WinPeToolPaths tools,
        bool useBootEx,
        CancellationToken cancellationToken = default) =>
        RunUsbFileOperationAsync(() => UpdateBootPartitionCoreAsync(options, artifact, tools, useBootEx, cancellationToken));

    /// <summary>Translates device-backed filesystem failures before callers log the result.</summary>
    private static async Task<WinPeResult<WinPeUsbProvisionResult>> RunUsbFileOperationAsync(
        Func<Task<WinPeResult<WinPeUsbProvisionResult>>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(new WinPeDiagnostic(
                WinPeErrorCodes.UsbCopyFailed,
                "Unable to access the confirmed USB media filesystem.",
                "A BOOT or CACHE filesystem operation failed.",
                failureKind: WinPeFailureKinds.FileSystem,
                failureReason: ex is IOException ? WinPeFailureReasons.IoError : WinPeFailureReasons.AccessDenied));
        }
    }

    private async Task<WinPeResult<WinPeUsbProvisionResult>> ProvisionAndPopulateCoreAsync(
        UsbOutputOptions options,
        WinPeBuildArtifact artifact,
        WinPeToolPaths tools,
        bool useBootEx,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(tools);

        cancellationToken.ThrowIfCancellationRequested();

        if (!options.TargetDiskNumber.HasValue)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(
                WinPeErrorCodes.ValidationFailed,
                "USB target disk number is required.",
                "Set UsbOutputOptions.TargetDiskNumber to the physical disk number you intend to erase.");
        }

        int diskNumber = options.TargetDiskNumber.Value;
        ReportProgress(options.Progress, 0, "Validating USB target.");
        WinPeResult<IReadOnlyList<WinPeUsbDiskIdentity>> diskResult = await GetDiskIdentitiesAsync(
            tools,
            artifact.WorkingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!diskResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(diskResult.Error!);
        }

        ReportProgress(options.Progress, 10, "Checking USB target safety.");
        IReadOnlyList<WinPeUsbDiskIdentity> disks = diskResult.Value!;
        WinPeUsbDiskIdentity? disk = disks.FirstOrDefault(candidate => candidate.Number == diskNumber);
        WinPeResult safetyValidation = disk is null
            ? WinPeResult.Failure(WinPeErrorCodes.UsbIdentityMismatch, "Confirmed USB disk is no longer present.")
            : ValidateDiskSafety(options, disk, disks);
        if (!safetyValidation.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(safetyValidation.Error!);
        }

        ReportProgress(options.Progress, 20, "Partitioning and formatting USB target.");
        WinPeResult<WinPeUsbProvisionResult> provisioningResult = await ProvisionDiskAsync(
            options.ExpectedDisk!,
            options.PartitionStyle,
            options.FormatMode,
            tools,
            artifact.WorkingDirectoryPath,
            options.Progress,
            cancellationToken).ConfigureAwait(false);
        if (!provisioningResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(provisioningResult.Error!);
        }

        WinPeUsbProvisionResult provisionedUsb = provisioningResult.Value!;
        WinPeResult copyLayoutValidation = await ValidatePopulationLayoutAsync(options.ExpectedDisk!, provisionedUsb, tools, artifact.WorkingDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!copyLayoutValidation.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(copyLayoutValidation.Error!);
        }

        string bootRootPath = _resolveVolumeRoot(provisionedUsb.BootVolumePath);
        string cacheRootPath = _resolveVolumeRoot(provisionedUsb.CacheVolumePath);
        ReportProgress(options.Progress, 55, "Copying WinPE media to USB.");
        WinPeResult copyResult = await CopyMediaAsync(
            artifact.MediaDirectoryPath,
            bootRootPath,
            artifact.WorkingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!copyResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(copyResult.Error!);
        }

        if (useBootEx)
        {
            ReportProgress(options.Progress, 70, "Configuring USB boot files.");
            WinPeResult bootFilesLayoutValidation = await ValidatePopulationLayoutAsync(options.ExpectedDisk!, provisionedUsb, tools, artifact.WorkingDirectoryPath, cancellationToken).ConfigureAwait(false);
            if (!bootFilesLayoutValidation.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(bootFilesLayoutValidation.Error!);
            }

            WinPeResult bootConfigurationResult = ConfigureBootFiles(bootRootPath, artifact);
            if (!bootConfigurationResult.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(bootConfigurationResult.Error!);
            }
        }

        ReportProgress(options.Progress, 78, "Verifying USB boot media.");
        WinPeResult verificationResult = VerifyBootArtifacts(bootRootPath, artifact.Architecture);
        if (!verificationResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(verificationResult.Error!);
        }

        WinPeResult bootLayoutResult = VerifyBootPartitionLayout(bootRootPath);
        if (!bootLayoutResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(bootLayoutResult.Error!);
        }

        ReportProgress(options.Progress, 85, "Preparing USB cache partition.");
        WinPeResult cacheLayoutValidation = await ValidatePopulationLayoutAsync(options.ExpectedDisk!, provisionedUsb, tools, artifact.WorkingDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!cacheLayoutValidation.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(cacheLayoutValidation.Error!);
        }

        InitializeCachePartitionDirectories(cacheRootPath);

        if (options.RuntimePayloadProvisioning is not null)
        {
            ReportProgress(options.Progress, 92, "Provisioning USB runtime payloads.");
            WinPeResult runtimeLayoutValidation = await ValidatePopulationLayoutAsync(options.ExpectedDisk!, provisionedUsb, tools, artifact.WorkingDirectoryPath, cancellationToken).ConfigureAwait(false);
            if (!runtimeLayoutValidation.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(runtimeLayoutValidation.Error!);
            }

            WinPeResult runtimePayloadResult = await _runtimePayloadProvisioningService.ProvisionAsync(
                CreateUsbRuntimePayloadOptions(options.RuntimePayloadProvisioning, artifact, cacheRootPath),
                options.DownloadProgress,
                cancellationToken).ConfigureAwait(false);

            if (!runtimePayloadResult.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(runtimePayloadResult.Error! with
                {
                    Message = "Failed to provision USB runtime payloads.",
                    Details = "Unable to populate CACHE/Runtime.",
                    Command = null,
                    ErrorSummary = null,
                    Exception = null
                });
            }
        }

        ReportProgress(options.Progress, 100, "USB media completed.");
        return WinPeResult<WinPeUsbProvisionResult>.Success(provisionedUsb);
    }

    private async Task<WinPeResult<WinPeUsbProvisionResult>> UpdateBootPartitionCoreAsync(
        UsbOutputOptions options,
        WinPeBuildArtifact artifact,
        WinPeToolPaths tools,
        bool useBootEx,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(tools);

        cancellationToken.ThrowIfCancellationRequested();

        if (!options.TargetDiskNumber.HasValue)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(
                WinPeErrorCodes.ValidationFailed,
                "USB target disk number is required.",
                "Set UsbOutputOptions.TargetDiskNumber to the physical disk number you intend to update.");
        }

        int diskNumber = options.TargetDiskNumber.Value;
        ReportProgress(options.Progress, 0, "Validating USB target.");
        WinPeResult<IReadOnlyList<WinPeUsbDiskIdentity>> diskResult = await GetDiskIdentitiesAsync(
            tools,
            artifact.WorkingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!diskResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(diskResult.Error!);
        }

        ReportProgress(options.Progress, 10, "Checking USB target safety.");
        IReadOnlyList<WinPeUsbDiskIdentity> disks = diskResult.Value!;
        WinPeUsbDiskIdentity? disk = disks.FirstOrDefault(candidate => candidate.Number == diskNumber);
        WinPeResult safetyValidation = disk is null
            ? WinPeResult.Failure(WinPeErrorCodes.UsbIdentityMismatch, "Confirmed USB disk is no longer present.")
            : ValidateDiskSafety(options, disk, disks);
        if (!safetyValidation.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(safetyValidation.Error!);
        }

        ReportProgress(options.Progress, 20, "Inspecting USB media layout.");
        WinPeResult<WinPeUsbProvisionResult> layoutResult = await GetFoundryUsbMediaLayoutAsync(
            options.ExpectedDisk!,
            tools,
            artifact.WorkingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!layoutResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(layoutResult.Error!);
        }

        WinPeUsbProvisionResult layout = layoutResult.Value!;
        ReportProgress(options.Progress, 35, "Formatting BOOT partition.");
        WinPeResult<WinPeUsbProvisionResult> formatResult = await FormatBootPartitionAsync(
            options.ExpectedDisk!,
            layout,
            options.FormatMode,
            tools,
            artifact.WorkingDirectoryPath,
            options.Progress,
            cancellationToken).ConfigureAwait(false);
        if (!formatResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(formatResult.Error!);
        }

        layout = formatResult.Value!;
        WinPeResult copyLayoutValidation = await ValidatePopulationLayoutAsync(options.ExpectedDisk!, layout, tools, artifact.WorkingDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!copyLayoutValidation.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(copyLayoutValidation.Error!);
        }

        string bootRootPath = _resolveVolumeRoot(layout.BootVolumePath);
        ReportProgress(options.Progress, 55, "Copying WinPE media to USB.");
        WinPeResult copyResult = await CopyMediaAsync(
            artifact.MediaDirectoryPath,
            bootRootPath,
            artifact.WorkingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!copyResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(copyResult.Error!);
        }

        if (useBootEx)
        {
            ReportProgress(options.Progress, 75, "Configuring USB boot files.");
            WinPeResult bootFilesLayoutValidation = await ValidatePopulationLayoutAsync(options.ExpectedDisk!, layout, tools, artifact.WorkingDirectoryPath, cancellationToken).ConfigureAwait(false);
            if (!bootFilesLayoutValidation.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(bootFilesLayoutValidation.Error!);
            }

            WinPeResult bootConfigurationResult = ConfigureBootFiles(bootRootPath, artifact);
            if (!bootConfigurationResult.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(bootConfigurationResult.Error!);
            }
        }

        ReportProgress(options.Progress, 90, "Verifying USB boot media.");
        WinPeResult verificationResult = VerifyBootArtifacts(bootRootPath, artifact.Architecture);
        if (!verificationResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(verificationResult.Error!);
        }

        WinPeResult bootLayoutResult = VerifyBootPartitionLayout(bootRootPath);
        if (!bootLayoutResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(bootLayoutResult.Error!);
        }

        if (options.RuntimePayloadProvisioning is not null)
        {
            string cacheRootPath = _resolveVolumeRoot(layout.CacheVolumePath);
            ReportProgress(options.Progress, 92, "Provisioning USB runtime payloads.");
            WinPeResult cacheLayoutValidation = await ValidatePopulationLayoutAsync(options.ExpectedDisk!, layout, tools, artifact.WorkingDirectoryPath, cancellationToken).ConfigureAwait(false);
            if (!cacheLayoutValidation.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(cacheLayoutValidation.Error!);
            }

            InitializeCachePartitionDirectories(cacheRootPath);
            WinPeResult runtimeLayoutValidation = await ValidatePopulationLayoutAsync(options.ExpectedDisk!, layout, tools, artifact.WorkingDirectoryPath, cancellationToken).ConfigureAwait(false);
            if (!runtimeLayoutValidation.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(runtimeLayoutValidation.Error!);
            }

            WinPeResult runtimePayloadResult = await _runtimePayloadProvisioningService.ProvisionAsync(
                CreateUsbRuntimePayloadOptions(options.RuntimePayloadProvisioning, artifact, cacheRootPath),
                options.DownloadProgress,
                cancellationToken).ConfigureAwait(false);

            if (!runtimePayloadResult.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(runtimePayloadResult.Error! with
                {
                    Message = "Failed to provision USB runtime payloads.",
                    Details = "Unable to populate CACHE/Runtime.",
                    Command = null,
                    ErrorSummary = null,
                    Exception = null
                });
            }
        }

        ReportProgress(options.Progress, 100, "USB boot partition updated.");
        return WinPeResult<WinPeUsbProvisionResult>.Success(layout);
    }

    /// <summary>Validates one confirmed snapshot against the same complete enumeration used for uniqueness.</summary>
    internal static WinPeResult ValidateDiskSafety(
        UsbOutputOptions options,
        WinPeUsbDiskIdentity disk,
        IReadOnlyList<WinPeUsbDiskIdentity> disks)
    {
        if (!IsSelectableDisk(disk))
        {
            return WinPeResult.Failure(WinPeErrorCodes.UsbUnsafeTarget, "USB target is not selectable.");
        }

        WinPeUsbDiskIdentity? expected = options.ExpectedDisk;
        if (expected is null || !IsSelectableDisk(expected) ||
            options.TargetDiskNumber != expected.Number || disk.Number != expected.Number ||
            disk.Size != expected.Size || CanonicalBus(disk.BusType) != CanonicalBus(expected.BusType) ||
            disks.Count(candidate => candidate.Number == expected.Number) != 1 ||
            !SameIdentifier(disk.UniqueId, expected.UniqueId) ||
            !SameIdentifier(disk.SerialNumber, expected.SerialNumber))
        {
            return WinPeResult.Failure(WinPeErrorCodes.UsbIdentityMismatch, "USB target no longer matches the confirmed disk.");
        }

        string uniqueId = expected.UniqueId.Trim();
        string serial = expected.SerialNumber.Trim();
        int matches = uniqueId.Length > 0
            ? disks.Count(candidate => SameIdentifier(candidate.UniqueId, uniqueId))
            : serial.Length > 0 ? disks.Count(candidate => SameIdentifier(candidate.SerialNumber, serial)) : 0;
        return matches == 1
            ? WinPeResult.Success()
            : WinPeResult.Failure(WinPeErrorCodes.UsbIdentityMismatch, "USB target identity is missing or ambiguous.");
    }

    private static bool IsSelectableDisk(WinPeUsbDiskIdentity disk) =>
        CanonicalBus(disk.BusType) == "USB" && disk.IsRemovable != false &&
        !disk.IsSystem && !disk.IsBoot && !disk.IsOffline && !disk.IsReadOnly &&
        disk.Size >= MinimumUsbDiskSizeBytes;

    private static string CanonicalBus(string value) => value.Trim().ToUpperInvariant();

    private static bool SameIdentifier(string first, string second) =>
        string.Equals(first.Trim(), second.Trim(), StringComparison.Ordinal);

    internal static bool IsRobocopySuccessExitCode(int exitCode)
    {
        return exitCode is >= 0 and <= 7;
    }

    internal static string BuildPowerShellProvisioningScript(
        WinPeUsbDiskIdentity expectedDisk,
        UsbPartitionStyle partitionStyle,
        UsbFormatMode formatMode)
    {
        string template = WinPeEmbeddedAssetService.ReadEmbeddedText(WinPeEmbeddedAssetService.UsbProvisioningScriptResourceName);
        return ReadUsbDiskOperations() + template
            .Replace("{{EXPECTED_DISK}}", EncodeJson(expectedDisk))
            .Replace("{{PARTITION_STYLE}}", partitionStyle == UsbPartitionStyle.Gpt ? "GPT" : "MBR")
            .Replace("{{FULL_FORMAT}}", formatMode == UsbFormatMode.Complete ? "$true" : "$false")
            .ReplaceLineEndings(Environment.NewLine);
    }

    internal static string BuildPowerShellBootPartitionUpdateScript(
        WinPeUsbDiskIdentity expectedDisk,
        WinPeUsbProvisionResult layout,
        UsbFormatMode formatMode)
    {
        return ReadUsbDiskOperations() + $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Storage
            $expected = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{EncodeJson(expectedDisk)}}')) | ConvertFrom-Json
            $layout = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{EncodeJson(layout)}}')) | ConvertFrom-Json
            $validatedLayout = Get-FoundryUsbLayout -Expected $expected -Layout $layout
            $bootPartition = Get-FoundryUsbLayoutPartition -Expected $expected -Layout $layout -Role 'Boot'
            $bootVolume = Get-FoundryUsbPartitionVolume $bootPartition
            if ([string]$bootVolume.UniqueId -cne $layout.BootVolumeUniqueId -or [string]$bootVolume.Path -cne $layout.BootVolumePath) { throw 'BOOT volume identity changed.' }
            Write-Output 'FOUNDRY_USB_PROGRESS|35|Formatting BOOT partition.'
            Format-Volume -InputObject $bootVolume -FileSystem FAT32 -NewFileSystemLabel BOOT -Full:{{(formatMode == UsbFormatMode.Complete ? "$true" : "$false")}} -Force -Confirm:$false -ErrorAction Stop | Out-Null
            $bootPartition = Get-FoundryUsbLayoutPartition -Expected $expected -Layout $layout -Role 'Boot'
            $current = Get-FoundryUsbLayout -Expected $expected
            foreach ($property in @('CachePartitionNumber', 'CachePartitionOffset', 'CachePartitionSize', 'CachePartitionGuid', 'CacheVolumeUniqueId', 'CacheVolumePath')) {
                if ([string]$current.$property -cne [string]$layout.$property) { throw 'CACHE identity changed.' }
            }
            if ($current.BootPartitionNumber -ne $bootPartition.PartitionNumber -or $current.BootPartitionOffset -ne $bootPartition.Offset -or $current.BootPartitionSize -ne $bootPartition.Size -or $current.BootPartitionGuid -cne [string]$bootPartition.Guid) { throw 'BOOT partition changed after formatting.' }
            $current | ConvertTo-Json -Depth 5 -Compress
            """.ReplaceLineEndings(Environment.NewLine);
    }

    private static string ReadUsbDiskOperations() =>
        WinPeEmbeddedAssetService.ReadEmbeddedText(WinPeEmbeddedAssetService.UsbDiskOperationsResourceName) + Environment.NewLine;

    private static string EncodeJson<T>(T value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    internal static void InitializeCachePartitionDirectories(string cacheRootPath)
    {
        Directory.CreateDirectory(Path.Combine(cacheRootPath, "Runtime"));
        Directory.CreateDirectory(Path.Combine(cacheRootPath, "Cache", "OperatingSystems"));
        Directory.CreateDirectory(Path.Combine(cacheRootPath, "Cache", "DriverPacks"));
        Directory.CreateDirectory(Path.Combine(cacheRootPath, "Cache", "Firmware"));
    }

    internal static WinPeRuntimePayloadProvisioningOptions CreateUsbRuntimePayloadOptions(
        WinPeRuntimePayloadProvisioningOptions options,
        WinPeBuildArtifact artifact,
        string cacheRootPath)
    {
        return options with
        {
            MountedImagePath = string.Empty,
            UsbCacheRootPath = cacheRootPath,
            WorkingDirectoryPath = string.IsNullOrWhiteSpace(options.WorkingDirectoryPath)
                ? artifact.WorkingDirectoryPath
                : options.WorkingDirectoryPath,
            Architecture = artifact.Architecture
        };
    }

    internal static WinPeResult ConfigureBootFiles(
        string bootRootPath,
        WinPeBuildArtifact artifact)
    {
        string bootManagerSourcePath = Path.Combine(artifact.WorkingDirectoryPath, "bootbins", "bootmgfw_EX.efi");
        if (!File.Exists(bootManagerSourcePath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.BootExUnsupported,
                "PCA2023 USB creation requires BootEx EFI binaries in the WinPE workspace.",
                "Expected workspace/bootbins/bootmgfw_EX.efi.");
        }

        string efiBootPath = Path.Combine(bootRootPath, "EFI", "Boot", artifact.Architecture.ToBootEfiName());
        if (File.Exists(efiBootPath))
        {
            File.Copy(bootManagerSourcePath, efiBootPath, overwrite: true);
        }

        string efiMicrosoftBootManagerPath = Path.Combine(bootRootPath, "EFI", "Microsoft", "Boot", "bootmgfw.efi");
        string? efiMicrosoftBootDirectoryPath = Path.GetDirectoryName(efiMicrosoftBootManagerPath);
        if (string.IsNullOrWhiteSpace(efiMicrosoftBootDirectoryPath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbProvisioningFailed,
                "USB boot configuration failed: EFI Microsoft boot manager path is invalid.",
                "Expected BOOT/EFI/Microsoft/Boot/bootmgfw.efi.");
        }

        Directory.CreateDirectory(efiMicrosoftBootDirectoryPath);
        File.Copy(bootManagerSourcePath, efiMicrosoftBootManagerPath, overwrite: true);
        return WinPeResult.Success();
    }

    internal static WinPeResult VerifyBootArtifacts(string bootRootPath, WinPeArchitecture architecture)
    {
        string bootWimPath = Path.Combine(bootRootPath, "sources", "boot.wim");
        if (!File.Exists(bootWimPath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbVerificationFailed,
                "USB verification failed: boot.wim not found.",
                "Expected BOOT/sources/boot.wim.");
        }

        string bcdPath = Path.Combine(bootRootPath, "boot", "BCD");
        if (!File.Exists(bcdPath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbVerificationFailed,
                "USB verification failed: BCD not found.",
                "Expected BOOT/boot/BCD.");
        }

        string efiBootPath = Path.Combine(bootRootPath, "EFI", "Boot", architecture.ToBootEfiName());
        if (!File.Exists(efiBootPath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbVerificationFailed,
                "USB verification failed: EFI boot file not found.",
                $"Expected BOOT/EFI/Boot/{architecture.ToBootEfiName()}.");
        }

        return WinPeResult.Success();
    }

    internal static WinPeResult VerifyBootPartitionLayout(string bootRootPath)
    {
        string foundryPath = Path.Combine(bootRootPath, "Foundry");
        if (Directory.Exists(foundryPath) || File.Exists(foundryPath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbVerificationFailed,
                "USB verification failed: BOOT partition contains Foundry runtime content.",
                "Unexpected BOOT/Foundry runtime content.");
        }

        return WinPeResult.Success();
    }

    private async Task<WinPeResult<WinPeUsbProvisionResult>> ProvisionDiskAsync(
        WinPeUsbDiskIdentity expectedDisk,
        UsbPartitionStyle partitionStyle,
        UsbFormatMode formatMode,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        IProgress<WinPeMediaProgress>? progress,
        CancellationToken cancellationToken)
    {
        string script = BuildPowerShellProvisioningScript(
            expectedDisk,
            partitionStyle,
            formatMode);

        Directory.CreateDirectory(workingDirectoryPath);
        string arguments = CreatePowerShellArguments(script);
        var provisioningOutput = new UsbProvisioningOutputForwarder(progress);
        WinPeProcessExecution execution = _processRunner is IWinPeProcessOutputRunner outputRunner
            ? await outputRunner.RunWithOutputAsync(
                tools.PowerShellPath,
                arguments,
                workingDirectoryPath,
                provisioningOutput.Report,
                null,
                cancellationToken).ConfigureAwait(false)
            : await _processRunner.RunAsync(
                tools.PowerShellPath,
                arguments,
                workingDirectoryPath,
                cancellationToken).ConfigureAwait(false);

        if (execution.IsSuccess)
        {
            return ParseUsbProvisionResult(execution);
        }

        return WinPeResult<WinPeUsbProvisionResult>.Failure(WithoutDeviceDetails(execution).ToFailureDiagnostic(
            WinPeErrorCodes.UsbProvisioningFailed,
            "Failed to partition and format the confirmed USB disk.", toolName: "PowerShell"));
    }

    private async Task<WinPeResult<WinPeUsbProvisionResult>> GetFoundryUsbMediaLayoutAsync(
        WinPeUsbDiskIdentity expectedDisk,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        CancellationToken cancellationToken,
        WinPeUsbProvisionResult? retainedLayout = null)
    {
        string script = ReadUsbDiskOperations() + $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Storage
            $expected = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{EncodeJson(expectedDisk)}}')) | ConvertFrom-Json
            $layout = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{EncodeJson(retainedLayout)}}')) | ConvertFrom-Json
            Get-FoundryUsbLayout -Expected $expected -Layout $layout | ConvertTo-Json -Depth 5 -Compress
            """;
        WinPeResult<string> result = await RunPowerShellAsync(script, tools, workingDirectoryPath, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? ParseUsbProvisionResult(new WinPeProcessExecution { ExitCode = 0, StandardOutput = result.Value! })
            : WinPeResult<WinPeUsbProvisionResult>.Failure(WinPeErrorCodes.UsbVerificationFailed, "Confirmed USB media layout could not be verified.");
    }

    private async Task<WinPeResult> ValidatePopulationLayoutAsync(
        WinPeUsbDiskIdentity expectedDisk,
        WinPeUsbProvisionResult retainedLayout,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        CancellationToken cancellationToken)
    {
        WinPeResult<WinPeUsbProvisionResult> result = await GetFoundryUsbMediaLayoutAsync(
            expectedDisk, tools, workingDirectoryPath, cancellationToken, retainedLayout).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WinPeResult.Failure(result.Error!);
        }

        WinPeUsbProvisionResult current = result.Value!;
        return retainedLayout.ConfirmedDisk == expectedDisk && current.ConfirmedDisk == expectedDisk &&
               retainedLayout.BootPartitionNumber == current.BootPartitionNumber &&
               retainedLayout.CachePartitionNumber == current.CachePartitionNumber &&
               retainedLayout.BootPartitionOffset == current.BootPartitionOffset &&
               retainedLayout.CachePartitionOffset == current.CachePartitionOffset &&
               retainedLayout.BootPartitionSize == current.BootPartitionSize &&
               retainedLayout.CachePartitionSize == current.CachePartitionSize &&
               retainedLayout.BootPartitionGuid == current.BootPartitionGuid &&
               retainedLayout.CachePartitionGuid == current.CachePartitionGuid &&
               retainedLayout.BootVolumeUniqueId == current.BootVolumeUniqueId &&
               retainedLayout.CacheVolumeUniqueId == current.CacheVolumeUniqueId &&
               retainedLayout.BootVolumePath == current.BootVolumePath &&
               retainedLayout.CacheVolumePath == current.CacheVolumePath
            ? WinPeResult.Success()
            : WinPeResult.Failure(WinPeErrorCodes.UsbIdentityMismatch, "USB disk or volume identity changed before population.");
    }

    private async Task<WinPeResult<WinPeUsbProvisionResult>> FormatBootPartitionAsync(
        WinPeUsbDiskIdentity expectedDisk,
        WinPeUsbProvisionResult layout,
        UsbFormatMode formatMode,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        IProgress<WinPeMediaProgress>? progress,
        CancellationToken cancellationToken)
    {
        string script = BuildPowerShellBootPartitionUpdateScript(
            expectedDisk,
            layout,
            formatMode);

        Directory.CreateDirectory(workingDirectoryPath);
        string arguments = CreatePowerShellArguments(script);
        var provisioningOutput = new UsbProvisioningOutputForwarder(progress);
        WinPeProcessExecution execution = _processRunner is IWinPeProcessOutputRunner outputRunner
            ? await outputRunner.RunWithOutputAsync(
                tools.PowerShellPath,
                arguments,
                workingDirectoryPath,
                provisioningOutput.Report,
                null,
                cancellationToken).ConfigureAwait(false)
            : await _processRunner.RunAsync(
                tools.PowerShellPath,
                arguments,
                workingDirectoryPath,
                cancellationToken).ConfigureAwait(false);

        return execution.IsSuccess
            ? ParseUsbProvisionResult(execution)
            : WinPeResult<WinPeUsbProvisionResult>.Failure(WithoutDeviceDetails(execution).ToFailureDiagnostic(WinPeErrorCodes.UsbProvisioningFailed, "Failed to format the confirmed USB BOOT partition.", toolName: "PowerShell"));
    }

    private static WinPeResult<WinPeUsbProvisionResult> ParseUsbProvisionResult(WinPeProcessExecution execution)
    {
        string[] lines = execution.StandardOutput.Split(
            [Environment.NewLine, "\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string line in lines.Reverse())
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                WinPeUsbProvisionResult? result = JsonSerializer.Deserialize<WinPeUsbProvisionResult>(
                    line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (result is null)
                {
                    break;
                }

                string bootDriveLetter = NormalizeDriveLetter(result.BootDriveLetter);
                string cacheDriveLetter = NormalizeDriveLetter(result.CacheDriveLetter);
                if (result.ConfirmedDisk is null || result.BootPartitionNumber <= 0 || result.CachePartitionNumber <= 0 ||
                    result.BootPartitionNumber == result.CachePartitionNumber ||
                    result.BootPartitionOffset == 0 || result.CachePartitionOffset == 0 ||
                    result.BootPartitionSize == 0 || result.CachePartitionSize == 0 ||
                    string.IsNullOrWhiteSpace(result.BootVolumeUniqueId) || string.IsNullOrWhiteSpace(result.CacheVolumeUniqueId) ||
                    result.BootVolumeUniqueId == result.CacheVolumeUniqueId ||
                    !IsVolumeGuidPath(result.BootVolumePath) || !IsVolumeGuidPath(result.CacheVolumePath) ||
                    result.BootVolumePath.Equals(result.CacheVolumePath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                return WinPeResult<WinPeUsbProvisionResult>.Success(result with
                {
                    BootDriveLetter = bootDriveLetter,
                    CacheDriveLetter = cacheDriveLetter
                });
            }
            catch (JsonException ex)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(
                    WinPeErrorCodes.UsbProvisioningFailed,
                    "Failed to parse USB provisioning result.",
                    ex.Message);
            }
        }

        return WinPeResult<WinPeUsbProvisionResult>.Failure(
            WinPeErrorCodes.UsbProvisioningFailed,
            "USB provisioning did not return a complete disk and volume identity.");
    }

    private async Task<WinPeResult> CopyMediaAsync(
        string sourceMediaDirectoryPath,
        string destinationBootRootPath,
        string workingDirectoryPath,
        CancellationToken cancellationToken)
    {
        string robocopyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "robocopy.exe");
        if (!File.Exists(robocopyPath))
        {
            robocopyPath = "robocopy.exe";
        }

        WinPeProcessExecution execution = await _processRunner.RunAsync(
            robocopyPath,
            $"{WinPeProcessRunner.Quote(sourceMediaDirectoryPath)} {WinPeProcessRunner.Quote(destinationBootRootPath)} /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP",
            workingDirectoryPath,
            cancellationToken).ConfigureAwait(false);

        if (IsRobocopySuccessExitCode(execution.ExitCode))
        {
            return WinPeResult.Success();
        }

        return WinPeResult.Failure(WithoutDeviceDetails(execution).ToFailureDiagnostic(
            WinPeErrorCodes.UsbCopyFailed,
            "Failed to copy WinPE media files to USB BOOT partition.",
            toolName: "robocopy"));
    }

    private static void ReportProgress(IProgress<WinPeMediaProgress>? progress, int percent, string status)
    {
        progress?.Report(new WinPeMediaProgress
        {
            Percent = Math.Clamp(percent, 0, 100),
            Status = status
        });
    }

    private sealed class UsbProvisioningOutputForwarder(IProgress<WinPeMediaProgress>? progress)
    {
        private int currentPercent = 20;
        private string currentStatus = "Partitioning and formatting USB target.";

        public void Report(string line)
        {
            if (progress is null)
            {
                return;
            }

            if (line.StartsWith(UsbProvisioningProgressPrefix, StringComparison.Ordinal))
            {
                string payload = line[UsbProvisioningProgressPrefix.Length..];
                string[] parts = payload.Split('|', 2);
                if (parts.Length != 2 || !int.TryParse(parts[0], out int percent))
                {
                    return;
                }

                currentPercent = percent;
                currentStatus = parts[1];
                ReportProgress(progress, currentPercent, currentStatus);
                return;
            }

            string verboseLine = line.StartsWith(UsbProvisioningVerbosePrefix, StringComparison.Ordinal)
                ? line[UsbProvisioningVerbosePrefix.Length..]
                : line;
            if (verboseLine.Length > 0 && verboseLine[0] == '{')
            {
                return;
            }

            progress.Report(new WinPeMediaProgress
            {
                Percent = currentPercent,
                Status = currentStatus,
                LogDetail = verboseLine
            });
        }
    }

    private async Task<WinPeResult<IReadOnlyList<WinPeUsbDiskIdentity>>> GetDiskIdentitiesAsync(
        WinPeToolPaths tools,
        string workingDirectoryPath,
        CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $disks = @(Get-Disk -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    Number = [int]$_.Number
                    FriendlyName = [string]$_.FriendlyName
                    SerialNumber = [string]$_.SerialNumber
                    UniqueId = [string]$_.UniqueId
                    BusType = [string]$_.BusType
                    IsRemovable = $_.IsRemovable
                    IsSystem = [bool]$_.IsSystem
                    IsBoot = [bool]$_.IsBoot
                    IsOffline = [bool]$_.IsOffline
                    IsReadOnly = [bool]$_.IsReadOnly
                    Size = [uint64]$_.Size
                }
            })
            ConvertTo-Json -InputObject $disks -Compress
            """;
        WinPeResult<string> result = await RunPowerShellAsync(script, tools, workingDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WinPeResult<IReadOnlyList<WinPeUsbDiskIdentity>>.Failure(result.Error!);
        }

        try
        {
            WinPeUsbDiskIdentity[] disks = JsonObjectSequence.Parse(result.Value!)
                .Select(element => element.Deserialize<WinPeUsbDiskIdentity>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)
                .ToArray();
            return WinPeResult<IReadOnlyList<WinPeUsbDiskIdentity>>.Success(disks);
        }
        catch (JsonException)
        {
            return WinPeResult<IReadOnlyList<WinPeUsbDiskIdentity>>.Failure(WinPeErrorCodes.UsbQueryFailed, "Failed to parse USB disk enumeration.");
        }
    }

    private async Task<WinPeResult<string>> RunPowerShellAsync(
        string script,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        CancellationToken cancellationToken)
    {
        WinPeProcessExecution execution = await _processRunner.RunAsync(
            tools.PowerShellPath,
            CreatePowerShellArguments(script),
            workingDirectoryPath,
            cancellationToken).ConfigureAwait(false);

        if (!execution.IsSuccess)
        {
            return WinPeResult<string>.Failure(WithoutDeviceDetails(execution).ToFailureDiagnostic(
                WinPeErrorCodes.UsbQueryFailed,
                "A required PowerShell USB query command failed.",
                toolName: "PowerShell"));
        }

        string output = execution.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            return WinPeResult<string>.Failure(
                WinPeErrorCodes.UsbQueryFailed,
                "A required PowerShell USB query command returned no data.",
                WithoutDeviceDetails(execution).ToDiagnosticText());
        }

        return WinPeResult<string>.Success(output);
    }

    private static IReadOnlyList<WinPeUsbDiskCandidate> ParseUsbCandidates(string json)
    {
        var candidates = new List<WinPeUsbDiskCandidate>();
        foreach (JsonElement element in JsonObjectSequence.Parse(json))
        {
            WinPeUsbDiskCandidate? candidate = ParseUsbCandidate(element);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static string CreatePowerShellArguments(string script)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(script));
        }

        string loader = $$"""
            $bytes = [Convert]::FromBase64String('{{Convert.ToBase64String(compressed.ToArray())}}')
            $stream = [IO.MemoryStream]::new($bytes, $false)
            $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
            $reader = [IO.StreamReader]::new($gzip, [Text.Encoding]::UTF8)
            try { $source = $reader.ReadToEnd() } finally { $reader.Dispose() }
            & ([scriptblock]::Create($source))
            """;
        return string.Join(
            ' ',
            [
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                .. PowerShellCommand.CreateEncodedArguments(loader)
            ]);
    }

    private static WinPeUsbDiskCandidate? ParseUsbCandidate(JsonElement element)
    {
        if (!TryGetInt32(element, "Number", out int diskNumber))
        {
            return null;
        }

        return new WinPeUsbDiskCandidate
        {
            DiskNumber = diskNumber,
            FriendlyName = GetString(element, "FriendlyName"),
            DriveLetters = GetString(element, "DriveLetters"),
            SerialNumber = GetString(element, "SerialNumber"),
            UniqueId = GetString(element, "UniqueId"),
            BusType = GetString(element, "BusType"),
            IsRemovable = GetNullableBool(element, "IsRemovable"),
            IsSystem = GetBool(element, "IsSystem"),
            IsBoot = GetBool(element, "IsBoot"),
            IsOffline = GetBool(element, "IsOffline"),
            IsReadOnly = GetBool(element, "IsReadOnly"),
            SizeBytes = GetUInt64(element, "Size"),
            IsFoundryMedia = GetBool(element, "IsFoundryMedia")
        };
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString()
            : string.Empty;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        return property.ValueKind == JsonValueKind.String &&
               bool.TryParse(property.GetString(), out bool parsed) &&
               parsed;
    }

    private static bool? GetNullableBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        return property.ValueKind == JsonValueKind.String &&
               bool.TryParse(property.GetString(), out bool parsed)
            ? parsed
            : null;
    }

    private static ulong GetUInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out ulong value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               ulong.TryParse(property.GetString(), out ulong parsed)
            ? parsed
            : 0;
    }

    private static WinPeProcessExecution WithoutDeviceDetails(WinPeProcessExecution execution) =>
        execution with
        {
            Arguments = string.Empty,
            StandardOutput = string.Empty,
            StandardError = execution.StandardError.Contains("USB partition style remained contradictory", StringComparison.Ordinal)
                ? "USB partition style remained contradictory after Clear-Disk; provisioning stopped."
                : string.Empty
        };

    private static bool IsVolumeGuidPath(string path) =>
        path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(@"}\", StringComparison.Ordinal) && path.Length == 49 &&
        Guid.TryParseExact(path.Substring(11, 36), "D", out _);

    private static string NormalizeDriveLetter(string value)
    {
        string normalizedValue = value.Trim();
        if (normalizedValue.Length == 1 && char.IsLetter(normalizedValue[0]))
        {
            return $"{char.ToUpperInvariant(normalizedValue[0])}:";
        }

        if (normalizedValue.Length == 2 &&
            char.IsLetter(normalizedValue[0]) &&
            normalizedValue[1] == ':')
        {
            return $"{char.ToUpperInvariant(normalizedValue[0])}:";
        }

        return string.Empty;
    }

    private static string UseValidatedVolumeRoot(string volumeRootPath)
    {
        return volumeRootPath;
    }
}
