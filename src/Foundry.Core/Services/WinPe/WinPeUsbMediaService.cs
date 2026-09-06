// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

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

        const string script = """
                              $foundryGptBootPartitionType = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'

                              function Get-FoundryUsbDriveLetter($DriveLetter) {
                                  if ($null -eq $DriveLetter) { return $null }
                                  if ($DriveLetter -is [char] -and [int][char]$DriveLetter -eq 0) { return $null }

                                  $text = ([string]$DriveLetter).Trim()
                                  if ($text.Length -eq 2 -and $text[1] -eq ':') { $text = $text.Substring(0, 1) }
                                  if ($text -match '^[A-Za-z]$') { return $text.ToUpperInvariant() }

                                  return $null
                              }

                              function Get-FoundryUsbDriveLetterText($DriveLetter) {
                                  $letter = Get-FoundryUsbDriveLetter $DriveLetter
                                  if ($null -eq $letter) { return '' }
                                  return "$($letter):"
                              }

                              function Get-FoundryUsbPartitionVolume($Partition) {
                                  $driveLetter = Get-FoundryUsbDriveLetter $Partition.DriveLetter
                                  if ($null -ne $driveLetter) {
                                      $volume = Get-Volume -DriveLetter $driveLetter -ErrorAction SilentlyContinue
                                  }
                                  else {
                                      $volume = Get-Volume -Partition $Partition -ErrorAction SilentlyContinue
                                  }

                                  if ($null -eq $volume) { return $null }

                                  [pscustomobject]@{
                                      PartitionNumber = [int]$Partition.PartitionNumber
                                      DriveLetter = Get-FoundryUsbDriveLetterText $Partition.DriveLetter
                                      FileSystemLabel = [string]$volume.FileSystemLabel
                                      FileSystem = [string]$volume.FileSystem
                                      GptType = [string]$Partition.GptType
                                      MbrType = [string]$Partition.MbrType
                                      IsActive = [bool]$Partition.IsActive
                                  }
                              }

                              $disks = Get-Disk | Where-Object { $_.BusType -eq 'USB' }
                              $result = @(
                              foreach ($disk in $disks) {
                                  $partitions = @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue)
                                  $volumes = @($partitions | ForEach-Object { Get-FoundryUsbPartitionVolume $_ })
                                  $letters = @(
                                      $volumes | Where-Object { $_.DriveLetter -ne '' } | ForEach-Object { $_.DriveLetter }
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
                    !candidate.IsBoot)
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

    public async Task<WinPeResult<WinPeUsbProvisionResult>> ProvisionAndPopulateAsync(
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
        WinPeResult<WinPeUsbDiskIdentity> diskResult = await GetDiskIdentityAsync(
            diskNumber,
            tools,
            artifact.WorkingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!diskResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(diskResult.Error!);
        }

        ReportProgress(options.Progress, 10, "Checking USB target safety.");
        WinPeResult safetyValidation = ValidateDiskSafety(options, diskResult.Value!);
        if (!safetyValidation.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(safetyValidation.Error!);
        }

        ReportProgress(options.Progress, 20, "Partitioning and formatting USB target.");
        WinPeResult<WinPeUsbProvisionResult> provisioningResult = await ProvisionDiskAsync(
            diskNumber,
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
        string bootRootPath = _resolveVolumeRoot($"{provisionedUsb.BootDriveLetter}\\");
        string cacheRootPath = _resolveVolumeRoot($"{provisionedUsb.CacheDriveLetter}\\");
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
        InitializeCachePartitionDirectories(cacheRootPath);

        if (options.RuntimePayloadProvisioning is not null)
        {
            ReportProgress(options.Progress, 92, "Provisioning USB runtime payloads.");
            WinPeResult runtimePayloadResult = await _runtimePayloadProvisioningService.ProvisionAsync(
                CreateUsbRuntimePayloadOptions(options.RuntimePayloadProvisioning, artifact, cacheRootPath),
                options.DownloadProgress,
                cancellationToken).ConfigureAwait(false);

            if (!runtimePayloadResult.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(runtimePayloadResult.Error!);
            }
        }

        ReportProgress(options.Progress, 100, "USB media completed.");
        return WinPeResult<WinPeUsbProvisionResult>.Success(provisionedUsb);
    }

    public async Task<WinPeResult<WinPeUsbProvisionResult>> UpdateBootPartitionAsync(
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
        WinPeResult<WinPeUsbDiskIdentity> diskResult = await GetDiskIdentityAsync(
            diskNumber,
            tools,
            artifact.WorkingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!diskResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(diskResult.Error!);
        }

        ReportProgress(options.Progress, 10, "Checking USB target safety.");
        WinPeResult safetyValidation = ValidateDiskSafety(options, diskResult.Value!);
        if (!safetyValidation.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(safetyValidation.Error!);
        }

        ReportProgress(options.Progress, 20, "Inspecting USB media layout.");
        WinPeResult<WinPeUsbProvisionResult> layoutResult = await GetFoundryUsbMediaLayoutAsync(
            diskNumber,
            tools,
            artifact.WorkingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!layoutResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(layoutResult.Error!);
        }

        WinPeUsbProvisionResult layout = layoutResult.Value!;
        ReportProgress(options.Progress, 35, "Formatting BOOT partition.");
        WinPeResult formatResult = await FormatBootPartitionAsync(
            diskNumber,
            layout.BootDriveLetter,
            options.FormatMode,
            tools,
            artifact.WorkingDirectoryPath,
            options.Progress,
            cancellationToken).ConfigureAwait(false);
        if (!formatResult.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(formatResult.Error!);
        }

        string bootRootPath = _resolveVolumeRoot($"{layout.BootDriveLetter}\\");
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
            string cacheRootPath = _resolveVolumeRoot($"{layout.CacheDriveLetter}\\");
            ReportProgress(options.Progress, 92, "Provisioning USB runtime payloads.");
            InitializeCachePartitionDirectories(cacheRootPath);
            WinPeResult runtimePayloadResult = await _runtimePayloadProvisioningService.ProvisionAsync(
                CreateUsbRuntimePayloadOptions(options.RuntimePayloadProvisioning, artifact, cacheRootPath),
                options.DownloadProgress,
                cancellationToken).ConfigureAwait(false);

            if (!runtimePayloadResult.IsSuccess)
            {
                return WinPeResult<WinPeUsbProvisionResult>.Failure(runtimePayloadResult.Error!);
            }
        }

        ReportProgress(options.Progress, 100, "USB boot partition updated.");
        return WinPeResult<WinPeUsbProvisionResult>.Success(layout);
    }

    internal static WinPeResult ValidateDiskSafety(UsbOutputOptions options, WinPeUsbDiskIdentity disk)
    {
        if (!disk.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbUnsafeTarget,
                "Target disk is not on USB bus.",
                $"Disk {disk.Number} bus type is '{disk.BusType}'. Only USB disks are allowed.");
        }

        if (disk.IsRemovable == false)
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbUnsafeTarget,
                "Target disk is not removable.",
                $"Disk {disk.Number} reports IsRemovable=false.");
        }

        if (disk.IsSystem || disk.IsBoot)
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbUnsafeTarget,
                "Refusing to modify a system or boot disk.",
                $"Disk {disk.Number}: IsSystem={disk.IsSystem}, IsBoot={disk.IsBoot}.");
        }

        if (disk.Size < MinimumUsbDiskSizeBytes)
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbUnsafeTarget,
                "Target USB disk is below the minimum supported size.",
                $"Disk {disk.Number} size is {disk.Size} bytes. Foundry OSD requires a USB disk of at least 16 GB.");
        }

        if (string.IsNullOrWhiteSpace(options.ExpectedDiskFriendlyName) &&
            string.IsNullOrWhiteSpace(options.ExpectedDiskSerialNumber) &&
            string.IsNullOrWhiteSpace(options.ExpectedDiskUniqueId))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.ValidationFailed,
                "Disk identity confirmation is required.",
                "Set at least one expected disk identity value before formatting USB media.");
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedDiskFriendlyName) &&
            !ContainsIgnoreCase(disk.FriendlyName, options.ExpectedDiskFriendlyName))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbIdentityMismatch,
                "Target disk friendly name does not match confirmation.",
                $"Expected contains '{options.ExpectedDiskFriendlyName}', actual '{disk.FriendlyName}'.");
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedDiskSerialNumber) &&
            !ContainsIgnoreCase(disk.SerialNumber, options.ExpectedDiskSerialNumber))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbIdentityMismatch,
                "Target disk serial number does not match confirmation.",
                $"Expected contains '{options.ExpectedDiskSerialNumber}', actual '{disk.SerialNumber}'.");
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedDiskUniqueId) &&
            !ContainsIgnoreCase(disk.UniqueId, options.ExpectedDiskUniqueId))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbIdentityMismatch,
                "Target disk unique ID does not match confirmation.",
                $"Expected contains '{options.ExpectedDiskUniqueId}', actual '{disk.UniqueId}'.");
        }

        return WinPeResult.Success();
    }

    internal static bool IsRobocopySuccessExitCode(int exitCode)
    {
        return exitCode is >= 0 and <= 7;
    }

    internal static string BuildPowerShellProvisioningScript(
        int diskNumber,
        UsbPartitionStyle partitionStyle,
        UsbFormatMode formatMode)
    {
        string template = WinPeEmbeddedAssetService.ReadEmbeddedText(WinPeEmbeddedAssetService.UsbProvisioningScriptResourceName);
        string partitionStyleText = partitionStyle == UsbPartitionStyle.Gpt ? "GPT" : "MBR";
        string fullFormatValue = formatMode == UsbFormatMode.Complete ? "$true" : "$false";

        return template
            .Replace("{{DISK_NUMBER}}", diskNumber.ToString())
            .Replace("{{PARTITION_STYLE}}", partitionStyleText)
            .Replace("{{FULL_FORMAT}}", fullFormatValue)
            .ReplaceLineEndings(Environment.NewLine);
    }

    internal static string BuildPowerShellBootPartitionUpdateScript(
        int diskNumber,
        string bootDriveLetter,
        UsbFormatMode formatMode)
    {
        string normalizedBootDriveLetter = NormalizeDriveLetter(bootDriveLetter).TrimEnd(':');
        string fullFormatValue = formatMode == UsbFormatMode.Complete ? "$true" : "$false";

        return $$"""
                 $ErrorActionPreference = 'Stop'

                 Import-Module Storage

                 function Write-FoundryUsbProgress([int]$Percent, [string]$Status) {
                     Write-Output ("FOUNDRY_USB_PROGRESS|{0}|{1}" -f $Percent, $Status)
                 }

                 function Write-FoundryUsbVerbose([string]$Message) {
                     Write-Output ("FOUNDRY_USB_VERBOSE|{0}" -f $Message)
                 }

                 $diskNumber = {{diskNumber}}
                 $bootDriveLetter = '{{normalizedBootDriveLetter}}'
                 $fullFormat = {{fullFormatValue}}

                 Write-FoundryUsbProgress 35 'Formatting BOOT partition.'
                 $bootPartition = Get-Partition -DiskNumber $diskNumber -ErrorAction Stop |
                     Where-Object { $_.DriveLetter -eq $bootDriveLetter } |
                     Select-Object -First 1
                 if ($null -eq $bootPartition) {
                     throw "BOOT partition $bootDriveLetter`: was not found on disk $diskNumber."
                 }

                 $bootVolume = Get-Volume -DriveLetter $bootDriveLetter -ErrorAction Stop
                 if ($bootVolume.FileSystemLabel -ne 'BOOT' -or $bootVolume.FileSystem -ne 'FAT32') {
                     throw "Volume $bootDriveLetter`: is not a Foundry BOOT volume. Label=$($bootVolume.FileSystemLabel), FileSystem=$($bootVolume.FileSystem)."
                 }

                 $bootFormatArguments = @{
                     DriveLetter = $bootDriveLetter
                     FileSystem = 'FAT32'
                     NewFileSystemLabel = 'BOOT'
                     Confirm = $false
                     Force = $true
                     ErrorAction = 'Stop'
                 }
                 if ($fullFormat) { $bootFormatArguments['Full'] = $true }
                 Format-Volume @bootFormatArguments | Out-Null
                 Write-FoundryUsbVerbose "BOOT partition formatted. DriveLetter=$bootDriveLetter, FileSystem=FAT32, Label=BOOT."
                 """.ReplaceLineEndings(Environment.NewLine);
    }

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
                $"Expected '{bootManagerSourcePath}'.");
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
                $"Expected '{efiMicrosoftBootManagerPath}'.");
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
                $"Expected '{bootWimPath}'.");
        }

        string bcdPath = Path.Combine(bootRootPath, "boot", "BCD");
        if (!File.Exists(bcdPath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbVerificationFailed,
                "USB verification failed: BCD not found.",
                $"Expected '{bcdPath}'.");
        }

        string efiBootPath = Path.Combine(bootRootPath, "EFI", "Boot", architecture.ToBootEfiName());
        if (!File.Exists(efiBootPath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.UsbVerificationFailed,
                "USB verification failed: EFI boot file not found.",
                $"Expected '{efiBootPath}'.");
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
                $"Unexpected path: '{foundryPath}'.");
        }

        return WinPeResult.Success();
    }

    private async Task<WinPeResult<WinPeUsbProvisionResult>> ProvisionDiskAsync(
        int diskNumber,
        UsbPartitionStyle partitionStyle,
        UsbFormatMode formatMode,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        IProgress<WinPeMediaProgress>? progress,
        CancellationToken cancellationToken)
    {
        string script = BuildPowerShellProvisioningScript(
            diskNumber,
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

        string diagnostic = $"{execution.ToDiagnosticText()}{Environment.NewLine}" +
                            $"PartitionStyle: {partitionStyle}{Environment.NewLine}" +
                            "PowerShellProvisioningScript:" + Environment.NewLine +
                            script;
        return WinPeResult<WinPeUsbProvisionResult>.Failure(
            WinPeErrorCodes.UsbProvisioningFailed,
            "Failed to partition and format the USB disk.",
            diagnostic);
    }

    private async Task<WinPeResult<WinPeUsbProvisionResult>> GetFoundryUsbMediaLayoutAsync(
        int diskNumber,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        CancellationToken cancellationToken)
    {
        string script = $$"""
                          $diskNumber = {{diskNumber}}
                          $foundryGptBootPartitionType = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'

                          function Get-FoundryUsbDriveLetter($DriveLetter) {
                              if ($null -eq $DriveLetter) { return $null }
                              if ($DriveLetter -is [char] -and [int][char]$DriveLetter -eq 0) { return $null }

                              $text = ([string]$DriveLetter).Trim()
                              if ($text.Length -eq 2 -and $text[1] -eq ':') { $text = $text.Substring(0, 1) }
                              if ($text -match '^[A-Za-z]$') { return $text.ToUpperInvariant() }

                              return $null
                          }

                          function Test-FoundryUsbDriveLetter($DriveLetter) {
                              return $null -ne (Get-FoundryUsbDriveLetter $DriveLetter)
                          }

                          function Get-FoundryUsbDriveLetterText($DriveLetter) {
                              $letter = Get-FoundryUsbDriveLetter $DriveLetter
                              if ($null -eq $letter) { return '' }
                              return "$($letter):"
                          }

                          function Get-FoundryUsbPartitionVolume($Partition) {
                              $driveLetter = Get-FoundryUsbDriveLetter $Partition.DriveLetter
                              if ($null -ne $driveLetter) {
                                  $volume = Get-Volume -DriveLetter $driveLetter -ErrorAction SilentlyContinue
                              }
                              else {
                                  $volume = Get-Volume -Partition $Partition -ErrorAction SilentlyContinue
                              }

                              if ($null -eq $volume) { return $null }

                              [pscustomobject]@{
                                  PartitionNumber = [int]$Partition.PartitionNumber
                                  DriveLetter = Get-FoundryUsbDriveLetterText $Partition.DriveLetter
                                  FileSystemLabel = [string]$volume.FileSystemLabel
                                  FileSystem = [string]$volume.FileSystem
                                  GptType = [string]$Partition.GptType
                                  MbrType = [string]$Partition.MbrType
                                  IsActive = [bool]$Partition.IsActive
                              }
                          }

                          $partitions = @(Get-Partition -DiskNumber $diskNumber -ErrorAction Stop)
                          $volumes = @($partitions | ForEach-Object { Get-FoundryUsbPartitionVolume $_ })

                          $bootVolume = @($volumes | Where-Object { $_.FileSystemLabel -eq 'BOOT' -and $_.FileSystem -eq 'FAT32' } | Select-Object -First 1)
                          $cacheVolume = @($volumes | Where-Object { $_.FileSystemLabel -eq 'Foundry Cache' -and $_.FileSystem -eq 'NTFS' } | Select-Object -First 1)
                          if ($cacheVolume.Count -eq 0) {
                              throw "Disk $diskNumber is not a Foundry USB media. Expected Foundry Cache NTFS volume."
                          }

                          if ($bootVolume.Count -gt 0) {
                              $bootPartition = @($partitions | Where-Object { $_.PartitionNumber -eq $bootVolume[0].PartitionNumber } | Select-Object -First 1)
                          }
                          else {
                              $bootPartition = @($partitions | Where-Object { [string]$_.GptType -eq $foundryGptBootPartitionType } | Select-Object -First 1)
                              if ($bootPartition.Count -eq 0) {
                                  $bootPartition = @($partitions | Where-Object { [string]$_.MbrType -eq 'FAT32' -and [bool]$_.IsActive } | Select-Object -First 1)
                              }

                              if ($bootPartition.Count -eq 0) {
                                  throw "Disk $diskNumber is not a Foundry USB media. Expected BOOT FAT32 partition."
                              }
                          }

                          $hasFoundryBootPartitionType = ([string]$bootPartition[0].GptType -eq $foundryGptBootPartitionType) -or ([string]$bootPartition[0].MbrType -eq 'FAT32' -and [bool]$bootPartition[0].IsActive)
                          if (-not $hasFoundryBootPartitionType) {
                              throw "Disk $diskNumber is not a Foundry USB media. Expected BOOT FAT32 partition."
                          }

                          if (-not (Test-FoundryUsbDriveLetter $bootPartition[0].DriveLetter)) {
                              Add-PartitionAccessPath -DiskNumber $diskNumber -PartitionNumber $bootPartition[0].PartitionNumber -AssignDriveLetter -ErrorAction Stop
                              Update-HostStorageCache -ErrorAction SilentlyContinue
                              Update-Disk -Number $diskNumber -ErrorAction SilentlyContinue

                              $partitions = @(Get-Partition -DiskNumber $diskNumber -ErrorAction Stop)
                              $bootPartition = @($partitions | Where-Object { $_.PartitionNumber -eq $bootPartition[0].PartitionNumber } | Select-Object -First 1)
                          }

                          $bootVolume = @(Get-FoundryUsbPartitionVolume $bootPartition[0])
                          if ($bootVolume.Count -eq 0 -or $bootVolume[0].FileSystemLabel -ne 'BOOT' -or $bootVolume[0].FileSystem -ne 'FAT32') {
                              throw "Disk $diskNumber is not a Foundry USB media. Expected BOOT FAT32 volume."
                          }

                          [pscustomobject]@{
                              DiskNumber = $diskNumber
                              BootDriveLetter = [string]$bootVolume[0].DriveLetter
                              CacheDriveLetter = [string]$cacheVolume[0].DriveLetter
                          } | ConvertTo-Json -Compress
                          """;

        WinPeResult<string> result = await RunPowerShellAsync(
            script,
            tools,
            workingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WinPeResult<WinPeUsbProvisionResult>.Failure(
                WinPeErrorCodes.UsbVerificationFailed,
                "Selected USB media is not a Foundry USB media.",
                result.Error?.Details ?? result.Error?.Message ?? string.Empty);
        }

        var execution = new WinPeProcessExecution
        {
            ExitCode = 0,
            StandardOutput = result.Value!
        };
        return ParseUsbProvisionResult(execution);
    }

    private async Task<WinPeResult> FormatBootPartitionAsync(
        int diskNumber,
        string bootDriveLetter,
        UsbFormatMode formatMode,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        IProgress<WinPeMediaProgress>? progress,
        CancellationToken cancellationToken)
    {
        string script = BuildPowerShellBootPartitionUpdateScript(
            diskNumber,
            bootDriveLetter,
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
            return WinPeResult.Success();
        }

        string diagnostic = $"{execution.ToDiagnosticText()}{Environment.NewLine}" +
                            "PowerShellBootPartitionUpdateScript:" + Environment.NewLine +
                            script;
        return WinPeResult.Failure(execution.ToFailureDiagnostic(
            WinPeErrorCodes.UsbProvisioningFailed,
            "Failed to format the USB BOOT partition.",
            toolName: "PowerShell") with
        { Details = diagnostic });
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
                if (string.IsNullOrWhiteSpace(bootDriveLetter) || string.IsNullOrWhiteSpace(cacheDriveLetter))
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
            "USB provisioning did not return assigned drive letters.",
            execution.ToDiagnosticText());
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

        return WinPeResult.Failure(execution.ToFailureDiagnostic(
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

    private async Task<WinPeResult<WinPeUsbDiskIdentity>> GetDiskIdentityAsync(
        int diskNumber,
        WinPeToolPaths tools,
        string workingDirectoryPath,
        CancellationToken cancellationToken)
    {
        string script = $$"""
                          $disk = Get-Disk -Number {{diskNumber}} -ErrorAction Stop
                          [pscustomobject]@{
                              Number = [int]$disk.Number
                              FriendlyName = [string]$disk.FriendlyName
                              SerialNumber = [string]$disk.SerialNumber
                              UniqueId = [string]$disk.UniqueId
                              BusType = [string]$disk.BusType
                              IsRemovable = $disk.IsRemovable
                              IsSystem = [bool]$disk.IsSystem
                              IsBoot = [bool]$disk.IsBoot
                              Size = [uint64]$disk.Size
                          } | ConvertTo-Json -Compress
                          """;

        WinPeResult<string> result = await RunPowerShellAsync(
            script,
            tools,
            workingDirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WinPeResult<WinPeUsbDiskIdentity>.Failure(result.Error!);
        }

        try
        {
            WinPeUsbDiskIdentity? disk = JsonSerializer.Deserialize<WinPeUsbDiskIdentity>(
                result.Value!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return disk is null
                ? WinPeResult<WinPeUsbDiskIdentity>.Failure(
                    WinPeErrorCodes.UsbQueryFailed,
                    "Failed to read target USB disk details.",
                    "PowerShell returned an empty payload for Get-Disk.")
                : WinPeResult<WinPeUsbDiskIdentity>.Success(disk);
        }
        catch (Exception ex)
        {
            return WinPeResult<WinPeUsbDiskIdentity>.Failure(
                WinPeErrorCodes.UsbQueryFailed,
                "Failed to parse target USB disk details.",
                ex.Message);
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
            return WinPeResult<string>.Failure(execution.ToFailureDiagnostic(
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
                execution.ToDiagnosticText());
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
        return string.Join(
            ' ',
            [
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                .. PowerShellCommand.CreateEncodedArguments(script)
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

    private static bool ContainsIgnoreCase(string source, string expectedFragment)
    {
        return source.IndexOf(expectedFragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

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
