// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.RegularExpressions;
using Foundry.Core.Services.WinPe;
using Foundry.Core.Tests.TestUtilities;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeUsbMediaServiceTests
{
    [Fact]
    public async Task GetUsbCandidatesAsync_FiltersUnsafeDisksAndParsesCandidates()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string payload = """
                         [
                           {"Number":3,"FriendlyName":"Safe USB","DriveLetters":"E:","SerialNumber":"USB123","UniqueId":"USB-ID","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000},
                           {"Number":4,"FriendlyName":"SATA Disk","DriveLetters":"F:","SerialNumber":"SATA123","UniqueId":"SATA-ID","BusType":"SATA","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000},
                           {"Number":5,"FriendlyName":"Fixed USB","DriveLetters":"G:","SerialNumber":"USB456","UniqueId":"USB-ID-2","BusType":"USB","IsRemovable":false,"IsSystem":false,"IsBoot":false,"Size":64000000000},
                           {"Number":6,"FriendlyName":"System USB","DriveLetters":"H:","SerialNumber":"USB789","UniqueId":"USB-ID-3","BusType":"USB","IsRemovable":true,"IsSystem":true,"IsBoot":false,"Size":64000000000}
                         ]
                         """;
        var service = new WinPeUsbMediaService(new FakeRunner(payload));

        WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>> result = await service.GetUsbCandidatesAsync(
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            workspace.RootPath,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        WinPeUsbDiskCandidate candidate = Assert.Single(result.Value!);
        Assert.Equal(3, candidate.DiskNumber);
        Assert.Equal("Safe USB", candidate.FriendlyName);
        Assert.Equal("E:", candidate.DriveLetters);
        Assert.Equal((ulong)64000000000, candidate.SizeBytes);
    }

    [Fact]
    public void ValidateDiskSafety_WhenTargetIsNotUsb_ReturnsUnsafeTarget()
    {
        WinPeResult result = WinPeUsbMediaService.ValidateDiskSafety(
            new UsbOutputOptions
            {
                TargetDiskNumber = 1,
                ExpectedDiskFriendlyName = "Internal"
            },
            new WinPeUsbDiskIdentity
            {
                Number = 1,
                FriendlyName = "Internal SSD",
                BusType = "NVMe",
                IsRemovable = true
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbUnsafeTarget, result.Error?.Code);
        Assert.Equal(WinPeFailureKinds.Validation, result.Error?.FailureKind);
        Assert.Equal(WinPeFailureReasons.DiskValidation, result.Error?.FailureReason);
    }

    [Fact]
    public void ValidateDiskSafety_WhenIdentityChanged_ReturnsIdentityMismatch()
    {
        WinPeResult result = WinPeUsbMediaService.ValidateDiskSafety(
            new UsbOutputOptions
            {
                TargetDiskNumber = 2,
                ExpectedDiskFriendlyName = "Expected USB",
                ExpectedDiskSerialNumber = "SERIAL-1",
                ExpectedDiskUniqueId = "UNIQUE-1"
            },
            new WinPeUsbDiskIdentity
            {
                Number = 2,
                FriendlyName = "Other USB",
                SerialNumber = "SERIAL-2",
                UniqueId = "UNIQUE-2",
                BusType = "USB",
                IsRemovable = true,
                Size = 64UL * 1024UL * 1024UL * 1024UL
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbIdentityMismatch, result.Error?.Code);
        Assert.Equal(WinPeFailureKinds.Validation, result.Error?.FailureKind);
        Assert.Equal(WinPeFailureReasons.DiskValidation, result.Error?.FailureReason);
    }

    [Fact]
    public void ValidateDiskSafety_WhenTargetIsBootDisk_ReturnsUnsafeTarget()
    {
        WinPeResult result = WinPeUsbMediaService.ValidateDiskSafety(
            new UsbOutputOptions
            {
                TargetDiskNumber = 3,
                ExpectedDiskFriendlyName = "Boot USB"
            },
            new WinPeUsbDiskIdentity
            {
                Number = 3,
                FriendlyName = "Boot USB",
                BusType = "USB",
                IsRemovable = true,
                IsBoot = true
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbUnsafeTarget, result.Error?.Code);
    }

    [Fact]
    public void ValidateDiskSafety_WhenTargetIsBelowMinimumSize_ReturnsUnsafeTarget()
    {
        WinPeResult result = WinPeUsbMediaService.ValidateDiskSafety(
            new UsbOutputOptions
            {
                TargetDiskNumber = 3,
                ExpectedDiskFriendlyName = "Small USB"
            },
            new WinPeUsbDiskIdentity
            {
                Number = 3,
                FriendlyName = "Small USB",
                BusType = "USB",
                IsRemovable = true,
                Size = 15UL * 1024UL * 1024UL * 1024UL
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbUnsafeTarget, result.Error?.Code);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void IsRobocopySuccessExitCode_AcceptsDocumentedSuccessRange(int exitCode, bool expected)
    {
        Assert.Equal(expected, WinPeUsbMediaService.IsRobocopySuccessExitCode(exitCode));
    }

    [Fact]
    public void VerifyBootArtifacts_WhenRequiredFilesExist_ReturnsSuccess()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "sources"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "boot"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "EFI", "Boot"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "sources", "boot.wim"), "wim");
        File.WriteAllText(Path.Combine(workspace.RootPath, "boot", "BCD"), "bcd");
        File.WriteAllText(Path.Combine(workspace.RootPath, "EFI", "Boot", "bootx64.efi"), "efi");

        WinPeResult result = WinPeUsbMediaService.VerifyBootArtifacts(workspace.RootPath, WinPeArchitecture.X64);

        Assert.True(result.IsSuccess, result.Error?.Details);
    }

    [Fact]
    public void VerifyBootArtifacts_WhenBootWimIsMissing_ReturnsVerificationFailed()
    {
        using TempWorkspace workspace = TempWorkspace.Create();

        WinPeResult result = WinPeUsbMediaService.VerifyBootArtifacts(workspace.RootPath, WinPeArchitecture.X64);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbVerificationFailed, result.Error?.Code);
    }

    [Fact]
    public void VerifyBootPartitionLayout_WhenFoundryDirectoryExistsOnBootPartition_ReturnsVerificationFailed()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "Foundry"));

        WinPeResult result = WinPeUsbMediaService.VerifyBootPartitionLayout(workspace.RootPath);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbVerificationFailed, result.Error?.Code);
    }

    [Fact]
    public void VerifyBootPartitionLayout_WhenBootPartitionIsMinimal_ReturnsSuccess()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "Boot"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "EFI"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "sources"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "bootmgr"), "boot");
        File.WriteAllText(Path.Combine(workspace.RootPath, "bootmgr.efi"), "efi");

        WinPeResult result = WinPeUsbMediaService.VerifyBootPartitionLayout(workspace.RootPath);

        Assert.True(result.IsSuccess, result.Error?.Details);
    }

    [Fact]
    public void BuildPowerShellProvisioningScript_WhenGptQuickFormat_CreatesBootAndCachePartitionsWithExplicitTypesWithoutActive()
    {
        string script = WinPeUsbMediaService.BuildPowerShellProvisioningScript(
            diskNumber: 7,
            partitionStyle: UsbPartitionStyle.Gpt,
            formatMode: UsbFormatMode.Quick);

        Assert.Contains("$diskNumber = 7", script, StringComparison.Ordinal);
        Assert.Contains("$partitionStyle = 'GPT'", script, StringComparison.Ordinal);
        Assert.Contains("Initialize-Disk -Number $diskNumber -PartitionStyle $partitionStyle", script, StringComparison.Ordinal);
        Assert.Contains("if ($partitionStyle -eq 'GPT')", script, StringComparison.Ordinal);
        Assert.Contains("Size = 2048MB", script, StringComparison.Ordinal);
        Assert.Contains("$bootPartitionArguments['GptType'] = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FileSystem = 'FAT32'", script, StringComparison.Ordinal);
        Assert.Contains("NewFileSystemLabel = 'BOOT'", script, StringComparison.Ordinal);
        Assert.Contains("$cachePartitionArguments['GptType'] = '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FileSystem = 'NTFS'", script, StringComparison.Ordinal);
        Assert.Contains("NewFileSystemLabel = 'Foundry Cache'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-IsActive $true", script, StringComparison.Ordinal);
        Assert.Contains("$fullFormat = $false", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPowerShellProvisioningScript_WhenClearedDiskKeepsPreviousStyle_ResetsPartitionStyle()
    {
        string script = WinPeUsbMediaService.BuildPowerShellProvisioningScript(
            diskNumber: 7,
            partitionStyle: UsbPartitionStyle.Gpt,
            formatMode: UsbFormatMode.Quick);

        Assert.Contains("& diskpart.exe /s $diskPartResetScriptPath", script, StringComparison.Ordinal);
        Assert.Contains("'clean'", script, StringComparison.Ordinal);
        Assert.Contains("\"convert $($partitionStyle.ToLowerInvariant())\"", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -Path $diskPartResetScriptPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be reset to $partitionStyle", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Disk -Number $diskNumber -PartitionStyle $partitionStyle", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPowerShellProvisioningScript_WhenFormattingFreshPartitions_UsesStorageAssignedDriveLetters()
    {
        string script = WinPeUsbMediaService.BuildPowerShellProvisioningScript(
            diskNumber: 7,
            partitionStyle: UsbPartitionStyle.Gpt,
            formatMode: UsbFormatMode.Quick);

        Assert.DoesNotContain("select partition", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("select volume", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("create partition", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("format fs=", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AssignDriveLetter = $true", script, StringComparison.Ordinal);
        Assert.Contains("$bootDriveLetter = $bootPartition.DriveLetter", script, StringComparison.Ordinal);
        Assert.Contains("$cacheDriveLetter = $cachePartition.DriveLetter", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("$bootDriveLetter = $bootPartition.DriveLetter", StringComparison.Ordinal) <
            script.IndexOf("$bootFormatArguments", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("$cacheDriveLetter = $cachePartition.DriveLetter", StringComparison.Ordinal) <
            script.IndexOf("$cacheFormatArguments", StringComparison.Ordinal));
        Assert.Contains("Format-Volume @bootFormatArguments", script, StringComparison.Ordinal);
        Assert.Contains("Format-Volume @cacheFormatArguments", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPowerShellProvisioningScript_WhenRecreatingUsb_ReleasesTargetAccessPathsBeforeClearingDisk()
    {
        string script = WinPeUsbMediaService.BuildPowerShellProvisioningScript(
            diskNumber: 7,
            partitionStyle: UsbPartitionStyle.Gpt,
            formatMode: UsbFormatMode.Quick);

        Assert.Contains("function Clear-FoundryUsbTargetAccessPaths", script, StringComparison.Ordinal);
        Assert.Contains("$partition.AccessPaths", script, StringComparison.Ordinal);
        Assert.Contains("Remove-PartitionAccessPath", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("Clear-FoundryUsbTargetAccessPaths", StringComparison.Ordinal) <
            script.IndexOf("Clear-Disk -Number $diskNumber", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("Clear-Disk -Number $diskNumber", StringComparison.Ordinal) <
            script.IndexOf("$bootPartition = New-Partition @bootPartitionArguments", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPowerShellProvisioningScript_WhenCreatingPartitions_WaitsForVolumesBeforeFormatting()
    {
        string script = WinPeUsbMediaService.BuildPowerShellProvisioningScript(
            diskNumber: 7,
            partitionStyle: UsbPartitionStyle.Gpt,
            formatMode: UsbFormatMode.Quick);

        Assert.Contains("function Wait-FoundryUsbVolume", script, StringComparison.Ordinal);
        Assert.Contains("Wait-FoundryUsbVolume -DriveLetter $bootDriveLetter -VolumeName 'BOOT'", script, StringComparison.Ordinal);
        Assert.Contains("Wait-FoundryUsbVolume -DriveLetter $cacheDriveLetter -VolumeName 'cache'", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("Wait-FoundryUsbVolume -DriveLetter $bootDriveLetter", StringComparison.Ordinal) <
            script.IndexOf("Format-Volume @bootFormatArguments", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("Wait-FoundryUsbVolume -DriveLetter $cacheDriveLetter", StringComparison.Ordinal) <
            script.IndexOf("Format-Volume @cacheFormatArguments", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPowerShellProvisioningScript_WhenFormattingUsb_EmitsProgressMarkers()
    {
        string script = WinPeUsbMediaService.BuildPowerShellProvisioningScript(
            diskNumber: 7,
            partitionStyle: UsbPartitionStyle.Gpt,
            formatMode: UsbFormatMode.Quick);

        Assert.Contains("FOUNDRY_USB_PROGRESS|", script, StringComparison.Ordinal);
        Assert.Contains("Write-FoundryUsbProgress 26 'Clearing USB partition table.'", script, StringComparison.Ordinal);
        Assert.Contains("Write-FoundryUsbProgress 44 'Formatting BOOT partition.'", script, StringComparison.Ordinal);
        Assert.Contains("Write-FoundryUsbProgress 53 'Formatting cache partition.'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPowerShellProvisioningScript_WhenFormattingUsb_EmitsVerboseMarkers()
    {
        string script = WinPeUsbMediaService.BuildPowerShellProvisioningScript(
            diskNumber: 7,
            partitionStyle: UsbPartitionStyle.Gpt,
            formatMode: UsbFormatMode.Quick);

        Assert.Contains("FOUNDRY_USB_VERBOSE|", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Verbose", script, StringComparison.Ordinal);
        Assert.Contains("Write-FoundryUsbVerbose \"Disk opened.", script, StringComparison.Ordinal);
        Assert.Contains("Write-FoundryUsbVerbose \"BOOT partition created.", script, StringComparison.Ordinal);
        Assert.Contains("Write-FoundryUsbVerbose \"Cache partition formatted.", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUsbCandidatesAsync_WhenDiskHasFoundryBootAndCacheVolumes_MarksFoundryMedia()
    {
        string payload = """
                         {"Number":9,"FriendlyName":"Safe USB","DriveLetters":"S:, T:","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000,"IsFoundryMedia":true}
                         """;
        var service = new WinPeUsbMediaService(new FakeRunner(payload));
        using TempWorkspace workspace = TempWorkspace.Create();

        WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>> result = await service.GetUsbCandidatesAsync(
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            workspace.RootPath,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        WinPeUsbDiskCandidate candidate = Assert.Single(result.Value!);
        Assert.True(candidate.IsFoundryMedia);
    }

    [Fact]
    public async Task GetUsbCandidatesAsync_QueryDetectsGptEfiBootPartitionWithoutDriveLetter()
    {
        string payload = """
                         {"Number":9,"FriendlyName":"Safe USB","DriveLetters":"T:","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000,"IsFoundryMedia":true}
                         """;
        var runner = new FakeRunner(payload);
        var service = new WinPeUsbMediaService(runner);
        using TempWorkspace workspace = TempWorkspace.Create();

        WinPeResult<IReadOnlyList<WinPeUsbDiskCandidate>> result = await service.GetUsbCandidatesAsync(
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            workspace.RootPath,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        WinPeUsbDiskCandidate candidate = Assert.Single(result.Value!);
        Assert.True(candidate.IsFoundryMedia);
        Assert.Equal("T:", candidate.DriveLetters);

        string script = DecodePowerShellEncodedCommand(runner.Executions[0].Arguments);
        Assert.Contains("$hasGptBootPartition", script, StringComparison.Ordinal);
        Assert.Contains("Get-Volume -Partition", script, StringComparison.Ordinal);
        Assert.Contains("GptType", script, StringComparison.Ordinal);
        Assert.Contains("{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$_.FileSystemLabel -eq 'BOOT' -and $_.FileSystem -eq 'FAT32'", script, StringComparison.Ordinal);
        Assert.Contains("$hasCacheVolume", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPowerShellProvisioningScript_WhenMbrCompleteFormat_MarksBootPartitionActiveAndFullFormat()
    {
        string script = WinPeUsbMediaService.BuildPowerShellProvisioningScript(
            diskNumber: 8,
            partitionStyle: UsbPartitionStyle.Mbr,
            formatMode: UsbFormatMode.Complete);

        Assert.Contains("$diskNumber = 8", script, StringComparison.Ordinal);
        Assert.Contains("$partitionStyle = 'MBR'", script, StringComparison.Ordinal);
        Assert.Contains("$fullFormat = $true", script, StringComparison.Ordinal);
        Assert.Contains("Size = 2048MB", script, StringComparison.Ordinal);
        Assert.Contains("$bootPartitionArguments['MbrType'] = 'FAT32'", script, StringComparison.Ordinal);
        Assert.Contains("$bootPartitionArguments['IsActive'] = $true", script, StringComparison.Ordinal);
        Assert.Contains("$cachePartitionArguments['MbrType'] = 'IFS'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Partition -DiskNumber $diskNumber -PartitionNumber $bootPartition.PartitionNumber -IsActive $true", script, StringComparison.Ordinal);
        Assert.Contains("if ($fullFormat) { $bootFormatArguments['Full'] = $true }", script, StringComparison.Ordinal);
        Assert.Contains("if ($fullFormat) { $cacheFormatArguments['Full'] = $true }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPowerShellBootPartitionUpdateScript_WhenUpdatingUsb_FormatsOnlyExistingBootVolume()
    {
        string script = WinPeUsbMediaService.BuildPowerShellBootPartitionUpdateScript(
            diskNumber: 9,
            bootDriveLetter: "S:",
            formatMode: UsbFormatMode.Quick);

        Assert.Contains("$diskNumber = 9", script, StringComparison.Ordinal);
        Assert.Contains("$bootDriveLetter = 'S'", script, StringComparison.Ordinal);
        Assert.Contains("Get-Partition -DiskNumber $diskNumber", script, StringComparison.Ordinal);
        Assert.Contains("Format-Volume @bootFormatArguments", script, StringComparison.Ordinal);
        Assert.Contains("NewFileSystemLabel = 'BOOT'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear-Disk", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Initialize-Disk", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New-Partition", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Foundry Cache", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitializeCachePartitionDirectories_CreatesOnlyMediaCreationCacheLayout()
    {
        using TempWorkspace workspace = TempWorkspace.Create();

        WinPeUsbMediaService.InitializeCachePartitionDirectories(workspace.RootPath);

        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "Runtime")));
        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "Cache", "OperatingSystems")));
        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "Cache", "DriverPacks")));
        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "Cache", "Firmware")));
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, "State")));
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, "Temp")));
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, "Logs")));
    }

    [Fact]
    public void CreateUsbRuntimePayloadOptions_ScopesRuntimeToCachePartition()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        var runtimeOptions = new WinPeRuntimePayloadProvisioningOptions
        {
            MountedImagePath = Path.Combine(workspace.RootPath, "mount"),
            UsbCacheRootPath = Path.Combine(workspace.RootPath, "old-cache"),
            WorkingDirectoryPath = Path.Combine(workspace.RootPath, "runtime-work"),
            Connect = new WinPeRuntimePayloadApplicationOptions { IsEnabled = true },
            Deploy = new WinPeRuntimePayloadApplicationOptions { IsEnabled = true }
        };
        var artifact = new WinPeBuildArtifact
        {
            WorkingDirectoryPath = Path.Combine(workspace.RootPath, "work"),
            Architecture = WinPeArchitecture.Arm64
        };
        string cacheRoot = Path.Combine(workspace.RootPath, "cache");

        WinPeRuntimePayloadProvisioningOptions result = WinPeUsbMediaService.CreateUsbRuntimePayloadOptions(
            runtimeOptions,
            artifact,
            cacheRoot);

        Assert.Equal(cacheRoot, result.UsbCacheRootPath);
        Assert.Equal(string.Empty, result.MountedImagePath);
        Assert.Equal(WinPeArchitecture.Arm64, result.Architecture);
        Assert.Same(runtimeOptions.Connect, result.Connect);
        Assert.Same(runtimeOptions.Deploy, result.Deploy);
    }

    [Fact]
    public void ConfigureBootFiles_WhenBootExBinaryExists_ReplacesArchitectureAndMicrosoftBootManagers()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string bootRoot = Path.Combine(workspace.RootPath, "boot-root");
        string workingDirectory = Path.Combine(workspace.RootPath, "work");
        Directory.CreateDirectory(Path.Combine(bootRoot, "EFI", "Boot"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, "bootbins"));
        string bootExPath = Path.Combine(workingDirectory, "bootbins", "bootmgfw_EX.efi");
        File.WriteAllText(bootExPath, "bootex");
        File.WriteAllText(Path.Combine(bootRoot, "EFI", "Boot", "bootx64.efi"), "old");

        WinPeResult result = WinPeUsbMediaService.ConfigureBootFiles(
            bootRoot,
            new WinPeBuildArtifact
            {
                WorkingDirectoryPath = workingDirectory,
                Architecture = WinPeArchitecture.X64
            });

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.Equal("bootex", File.ReadAllText(Path.Combine(bootRoot, "EFI", "Boot", "bootx64.efi")));
        Assert.Equal("bootex", File.ReadAllText(Path.Combine(bootRoot, "EFI", "Microsoft", "Boot", "bootmgfw.efi")));
    }

    [Fact]
    public async Task ProvisionAndPopulateAsync_WhenTargetDiskNumberIsMissing_ReturnsValidationFailure()
    {
        var service = new WinPeUsbMediaService(new FakeRunner("{}"));

        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions(),
            new WinPeBuildArtifact(),
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.ValidationFailed, result.Error?.Code);
    }

    [Fact]
    public async Task ProvisionAndPopulateAsync_WhenTargetIdentityIsUnsafe_ReturnsUnsafeTargetBeforeFormatting()
    {
        string payload = """
                         {"Number":9,"FriendlyName":"Internal SSD","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"NVMe","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000}
                         """;
        var runner = new FakeRunner(payload);
        using TempWorkspace workspace = TempWorkspace.Create();
        var service = new WinPeUsbMediaService(runner);

        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDiskFriendlyName = "Internal"
            },
            new WinPeBuildArtifact { WorkingDirectoryPath = workspace.RootPath },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbUnsafeTarget, result.Error?.Code);
        Assert.Single(runner.Executions);
        Assert.Contains("EncodedCommand", runner.Executions[0].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionAndPopulateAsync_WhenPartitioningUsb_UsesPowerShellStorageProvisioning()
    {
        string diskIdentity = """
                              {"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000}
                              """;
        string provisioningResult = """
                                    {"DiskNumber":9,"BootDriveLetter":"Y:","CacheDriveLetter":"Z:"}
                                    """;
        using var temporary = new TemporaryDirectory();
        string bootRoot = Path.Combine(temporary.Path, "boot");
        string cacheRoot = Path.Combine(temporary.Path, "cache");
        string mediaRoot = Path.Combine(temporary.Path, "media");
        Directory.CreateDirectory(bootRoot);
        Directory.CreateDirectory(cacheRoot);
        CreateVerifiedBootPartition(mediaRoot, WinPeArchitecture.X64);
        string ResolveTestRoot(string volume) => volume switch
        {
            "Y:\\" => bootRoot,
            "Z:\\" => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("S:\\"));
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 0,
            diskIdentity,
            provisioningResult,
            string.Empty);
        var service = new WinPeUsbMediaService(runner, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDiskFriendlyName = "Safe USB",
                PartitionStyle = UsbPartitionStyle.Gpt,
                FormatMode = UsbFormatMode.Quick
            },
            new WinPeBuildArtifact
            {
                WorkingDirectoryPath = temporary.Path,
                MediaDirectoryPath = mediaRoot,
                Architecture = WinPeArchitecture.X64
            },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.Equal("Y:", result.Value?.BootDriveLetter);
        Assert.Equal("Z:", result.Value?.CacheDriveLetter);
        Assert.True(File.Exists(Path.Combine(bootRoot, "sources", "boot.wim")));
        Assert.True(Directory.Exists(Path.Combine(cacheRoot, "Cache", "OperatingSystems")));
        Assert.DoesNotContain("diskpart.exe", runner.Executions.Select(execution => execution.FileName));
        Assert.Equal(2, runner.Executions.Count(execution => execution.FileName == "pwsh.exe"));
        Assert.Contains(runner.Executions, execution => execution.FileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProvisionAndPopulateAsync_WhenProvisioningAssignsDriveLetters_UsesReturnedLetters()
    {
        string diskIdentity = """
                              {"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000}
                              """;
        string provisioningResult = """
                                    FOUNDRY_USB_PROGRESS|55|USB partitions formatted.
                                    {"DiskNumber":9,"BootDriveLetter":"Y:","CacheDriveLetter":"Z:"}
                                    """;
        using var temporary = new TemporaryDirectory();
        string bootRoot = Path.Combine(temporary.Path, "boot");
        string cacheRoot = Path.Combine(temporary.Path, "cache");
        string mediaRoot = Path.Combine(temporary.Path, "media");
        Directory.CreateDirectory(bootRoot);
        Directory.CreateDirectory(cacheRoot);
        CreateVerifiedBootPartition(mediaRoot, WinPeArchitecture.X64);
        string ResolveTestRoot(string volume) => volume switch
        {
            "Y:\\" => bootRoot,
            "Z:\\" => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("S:\\"));
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 1,
            diskIdentity,
            provisioningResult,
            string.Empty);
        var service = new WinPeUsbMediaService(runner, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDiskFriendlyName = "Safe USB",
                PartitionStyle = UsbPartitionStyle.Gpt,
                FormatMode = UsbFormatMode.Quick
            },
            new WinPeBuildArtifact
            {
                WorkingDirectoryPath = temporary.Path,
                MediaDirectoryPath = mediaRoot,
                Architecture = WinPeArchitecture.X64
            },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.Equal("Y:", result.Value?.BootDriveLetter);
        Assert.Equal("Z:", result.Value?.CacheDriveLetter);
        WinPeProcessExecution copyExecution = Assert.Single(
            runner.Executions,
            execution => execution.FileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(bootRoot, copyExecution.Arguments, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(bootRoot, "sources", "boot.wim")));
    }

    [Fact]
    public async Task ProvisionAndPopulateAsync_WhenProvisioningStreamsOutput_ReportsProvisioningSubstepsAndVerboseDetails()
    {
        string payload = """
                         {"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000}
                         """;
        string provisioningResult = """
                                    FOUNDRY_USB_PROGRESS|55|USB partitions formatted.
                                    {"DiskNumber":9,"BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                                    """;
        using var temporary = new TemporaryDirectory();
        string bootRoot = Path.Combine(temporary.Path, "boot");
        string cacheRoot = Path.Combine(temporary.Path, "cache");
        string mediaRoot = Path.Combine(temporary.Path, "media");
        Directory.CreateDirectory(bootRoot);
        Directory.CreateDirectory(cacheRoot);
        CreateVerifiedBootPartition(mediaRoot, WinPeArchitecture.X64);
        string ResolveTestRoot(string volume) => volume switch
        {
            "S:\\" => bootRoot,
            "T:\\" => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("Y:\\"));
        var runner = new FakeOutputRunner(payload, provisioningResult);
        var progress = new RecordingProgress();
        var service = new WinPeUsbMediaService(runner, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDiskFriendlyName = "Safe USB",
                PartitionStyle = UsbPartitionStyle.Gpt,
                FormatMode = UsbFormatMode.Quick,
                Progress = progress
            },
            new WinPeBuildArtifact
            {
                WorkingDirectoryPath = temporary.Path,
                MediaDirectoryPath = mediaRoot,
                Architecture = WinPeArchitecture.X64
            },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.Contains(progress.Reports, report => report is { Percent: 26, Status: "Clearing USB partition table." });
        Assert.Contains(progress.Reports, report => report is { Percent: 44, Status: "Formatting BOOT partition." });
        Assert.Contains(progress.Reports, report => report is { Percent: 53, Status: "Formatting cache partition." });
        Assert.Contains(
            progress.Reports,
            report => report.Percent == 44 &&
                      report.Status == "Formatting BOOT partition." &&
                      report.LogDetail == "BOOT partition formatted. DriveLetter=S, FileSystem=FAT32, Label=BOOT.");
        Assert.DoesNotContain(progress.Reports, report => report.LogDetail?.StartsWith('{') == true);
    }

    [Fact]
    public async Task UpdateBootPartitionAsync_WhenSelectedDiskIsFoundryMedia_FormatsOnlyBootPartitionAndCopiesMedia()
    {
        string diskIdentity = """
                              {"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000}
                              """;
        string layout = """
                        {"DiskNumber":9,"BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                        """;
        string formatResult = """
                              FOUNDRY_USB_PROGRESS|35|Formatting BOOT partition.
                              {"DiskNumber":9,"BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                              """;
        using var temporary = new TemporaryDirectory();
        string bootRoot = Path.Combine(temporary.Path, "boot");
        string cacheRoot = Path.Combine(temporary.Path, "cache");
        string mediaRoot = Path.Combine(temporary.Path, "media");
        Directory.CreateDirectory(bootRoot);
        Directory.CreateDirectory(cacheRoot);
        CreateVerifiedBootPartition(mediaRoot, WinPeArchitecture.X64);
        string ResolveTestRoot(string volume) => volume switch
        {
            "S:\\" => bootRoot,
            "T:\\" => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("Y:\\"));
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 0,
            diskIdentity,
            layout,
            formatResult,
            string.Empty);
        var progress = new RecordingProgress();
        var service = new WinPeUsbMediaService(runner, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.UpdateBootPartitionAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDiskFriendlyName = "Safe USB",
                Progress = progress
            },
            new WinPeBuildArtifact
            {
                WorkingDirectoryPath = temporary.Path,
                MediaDirectoryPath = mediaRoot,
                Architecture = WinPeArchitecture.X64
            },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.Equal("S:", result.Value?.BootDriveLetter);
        Assert.Equal("T:", result.Value?.CacheDriveLetter);
        Assert.True(File.Exists(Path.Combine(bootRoot, "sources", "boot.wim")));
        Assert.Equal(3, runner.Executions.Count(execution => execution.FileName == "pwsh.exe"));
        Assert.Contains(runner.Executions, execution => execution.FileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(runner.Executions, execution => execution.Arguments.Contains("Clear-Disk", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Executions, execution => execution.Arguments.Contains("Foundry Cache", StringComparison.Ordinal));
        string layoutScript = DecodePowerShellEncodedCommand(runner.Executions[1].Arguments);
        Assert.Contains("Add-PartitionAccessPath", layoutScript, StringComparison.Ordinal);
        Assert.Contains("-AssignDriveLetter", layoutScript, StringComparison.Ordinal);
        Assert.Contains("Get-Volume -Partition", layoutScript, StringComparison.Ordinal);
        Assert.Contains("GptType", layoutScript, StringComparison.Ordinal);
        Assert.Contains("{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", layoutScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(progress.Reports, report => report is { Percent: 35, Status: "Formatting BOOT partition." });
        Assert.DoesNotContain(progress.Reports, report => report.Status.Contains("cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateBootPartitionAsync_WhenRuntimeProvisioningIsEnabled_RefreshesRuntimePayloadsOnCachePartition()
    {
        string diskIdentity = """
                              {"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000}
                              """;
        using var temporary = new TemporaryDirectory();
        string bootRoot = Path.Combine(temporary.Path, "boot");
        string cacheRoot = Path.Combine(temporary.Path, "cache");
        string mediaRoot = Path.Combine(temporary.Path, "media");
        Directory.CreateDirectory(bootRoot);
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(Path.Combine(cacheRoot, "preserve.txt"), "existing-cache");
        CreateVerifiedBootPartition(mediaRoot, WinPeArchitecture.Arm64);
        string ResolveTestRoot(string volume) => volume switch
        {
            "S:\\" => bootRoot,
            "T:\\" => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("Y:\\"));
        string layout = """
                        {"DiskNumber":9,"BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                        """;
        string formatResult = """
                              FOUNDRY_USB_PROGRESS|35|Formatting BOOT partition.
                              {"DiskNumber":9,"BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                              """;
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 0,
            diskIdentity,
            layout,
            formatResult,
            string.Empty);
        var runtimeProvisioningService = new FakeRuntimePayloadProvisioningService();
        var progress = new RecordingProgress();
        var downloadProgress = new CapturingProgress<WinPeDownloadProgress>();
        var service = new WinPeUsbMediaService(runner, runtimeProvisioningService, ResolveTestRoot);
        var runtimeOptions = new WinPeRuntimePayloadProvisioningOptions
        {
            WorkingDirectoryPath = Path.Combine(temporary.Path, "runtime-work"),
            MountedImagePath = Path.Combine(temporary.Path, "mount"),
            UsbCacheRootPath = Path.Combine(temporary.Path, "old-cache"),
            Connect = new WinPeRuntimePayloadApplicationOptions { IsEnabled = true },
            Deploy = new WinPeRuntimePayloadApplicationOptions { IsEnabled = true }
        };

        WinPeResult<WinPeUsbProvisionResult> result = await service.UpdateBootPartitionAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDiskFriendlyName = "Safe USB",
                RuntimePayloadProvisioning = runtimeOptions,
                DownloadProgress = downloadProgress,
                Progress = progress
            },
            new WinPeBuildArtifact
            {
                WorkingDirectoryPath = temporary.Path,
                MediaDirectoryPath = mediaRoot,
                Architecture = WinPeArchitecture.Arm64
            },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        WinPeRuntimePayloadProvisioningOptions capturedOptions = Assert.Single(runtimeProvisioningService.Options);
        Assert.Equal(cacheRoot, capturedOptions.UsbCacheRootPath);
        Assert.Equal(string.Empty, capturedOptions.MountedImagePath);
        Assert.Equal(WinPeArchitecture.Arm64, capturedOptions.Architecture);
        Assert.Equal(runtimeOptions.WorkingDirectoryPath, capturedOptions.WorkingDirectoryPath);
        Assert.Same(runtimeOptions.Connect, capturedOptions.Connect);
        Assert.Same(runtimeOptions.Deploy, capturedOptions.Deploy);
        Assert.Same(downloadProgress, Assert.Single(runtimeProvisioningService.DownloadProgress));
        Assert.Equal("existing-cache", File.ReadAllText(Path.Combine(cacheRoot, "preserve.txt")));
        Assert.True(Directory.Exists(Path.Combine(cacheRoot, "Cache", "OperatingSystems")));
        Assert.Contains(progress.Reports, report => report is { Percent: 92, Status: "Provisioning USB runtime payloads." });
    }

    [Fact]
    public async Task UpdateBootPartitionAsync_WhenRuntimeProvisioningFails_ReturnsFailure()
    {
        string diskIdentity = """
                              {"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000}
                              """;
        using var temporary = new TemporaryDirectory();
        string bootRoot = Path.Combine(temporary.Path, "boot");
        string cacheRoot = Path.Combine(temporary.Path, "cache");
        string mediaRoot = Path.Combine(temporary.Path, "media");
        Directory.CreateDirectory(bootRoot);
        Directory.CreateDirectory(cacheRoot);
        CreateVerifiedBootPartition(mediaRoot, WinPeArchitecture.X64);
        string ResolveTestRoot(string volume) => volume switch
        {
            "S:\\" => bootRoot,
            "T:\\" => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("Y:\\"));
        string layout = """
                        {"DiskNumber":9,"BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                        """;
        string formatResult = """
                              FOUNDRY_USB_PROGRESS|35|Formatting BOOT partition.
                              {"DiskNumber":9,"BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                              """;
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 0,
            diskIdentity,
            layout,
            formatResult,
            string.Empty);
        var runtimeProvisioningService = new FakeRuntimePayloadProvisioningService(
            WinPeResult.Failure(
                WinPeErrorCodes.BuildFailed,
                "Failed to provision Foundry runtime payloads.",
                "Runtime archive missing."));
        var progress = new RecordingProgress();
        var service = new WinPeUsbMediaService(runner, runtimeProvisioningService, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.UpdateBootPartitionAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDiskFriendlyName = "Safe USB",
                RuntimePayloadProvisioning = new WinPeRuntimePayloadProvisioningOptions
                {
                    WorkingDirectoryPath = Path.Combine(temporary.Path, "runtime-work"),
                    Connect = new WinPeRuntimePayloadApplicationOptions { IsEnabled = true }
                },
                Progress = progress
            },
            new WinPeBuildArtifact
            {
                WorkingDirectoryPath = temporary.Path,
                MediaDirectoryPath = mediaRoot,
                Architecture = WinPeArchitecture.X64
            },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.BuildFailed, result.Error?.Code);
        Assert.Contains(progress.Reports, report => report is { Percent: 92, Status: "Provisioning USB runtime payloads." });
        Assert.DoesNotContain(progress.Reports, report => report is { Percent: 100, Status: "USB boot partition updated." });
    }

    [Fact]
    public async Task UpdateBootPartitionAsync_WhenSelectedDiskIsNotFoundryMedia_ReturnsVerificationFailureBeforeFormatting()
    {
        string diskIdentity = """
                              {"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"IsSystem":false,"IsBoot":false,"Size":64000000000}
                              """;
        var runner = new FakeSequenceRunner(diskIdentity, string.Empty);
        using TempWorkspace workspace = TempWorkspace.Create();
        var service = new WinPeUsbMediaService(runner);

        WinPeResult<WinPeUsbProvisionResult> result = await service.UpdateBootPartitionAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDiskFriendlyName = "Safe USB"
            },
            new WinPeBuildArtifact
            {
                WorkingDirectoryPath = workspace.RootPath,
                MediaDirectoryPath = Path.Combine(workspace.RootPath, "media"),
                Architecture = WinPeArchitecture.X64
            },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" },
            useBootEx: false,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbVerificationFailed, result.Error?.Code);
        Assert.Equal(2, runner.Executions.Count);
        Assert.DoesNotContain(runner.Executions, execution => execution.FileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase));
    }

    private class FakeRunner(
        string output,
        bool copyMedia = false,
        int robocopyExitCode = 0) : IWinPeProcessRunner
    {
        public List<WinPeProcessExecution> Executions { get; } = [];

        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null)
        {
            bool isRobocopy = fileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase);
            if (isRobocopy && copyMedia && robocopyExitCode < 8)
            {
                CopyRobocopyMedia(arguments);
            }

            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                ExitCode = isRobocopy ? robocopyExitCode : 0,
                StandardOutput = output
            };
            Executions.Add(execution);
            return Task.FromResult(execution);
        }

        public Task<WinPeProcessExecution> RunCmdScriptAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSequenceRunner : IWinPeProcessRunner
    {
        private readonly bool _copyMedia;
        private readonly int _robocopyExitCode;
        private readonly Queue<string> _outputs;

        public FakeSequenceRunner(params string[] outputs)
            : this(copyMedia: false, robocopyExitCode: 0, outputs)
        {
        }

        public FakeSequenceRunner(bool copyMedia, int robocopyExitCode, params string[] outputs)
        {
            _copyMedia = copyMedia;
            _robocopyExitCode = robocopyExitCode;
            _outputs = new Queue<string>(outputs);
        }

        public List<WinPeProcessExecution> Executions { get; } = [];

        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null)
        {
            bool isRobocopy = fileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase);
            if (isRobocopy && _copyMedia && _robocopyExitCode < 8)
            {
                CopyRobocopyMedia(arguments);
            }

            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                ExitCode = isRobocopy ? _robocopyExitCode : 0,
                StandardOutput = _outputs.Count > 0 ? _outputs.Dequeue() : string.Empty
            };
            Executions.Add(execution);
            return Task.FromResult(execution);
        }

        public Task<WinPeProcessExecution> RunCmdScriptAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeOutputRunner(string output, string provisioningOutput)
        : FakeRunner(output, copyMedia: true), IWinPeProcessOutputRunner
    {
        public Task<WinPeProcessExecution> RunWithOutputAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            Action<string>? onOutputData,
            Action<string>? onErrorData,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null)
        {
            onOutputData?.Invoke("FOUNDRY_USB_PROGRESS|26|Clearing USB partition table.");
            onOutputData?.Invoke("FOUNDRY_USB_PROGRESS|44|Formatting BOOT partition.");
            onOutputData?.Invoke("FOUNDRY_USB_VERBOSE|BOOT partition formatted. DriveLetter=S, FileSystem=FAT32, Label=BOOT.");
            onOutputData?.Invoke("FOUNDRY_USB_PROGRESS|53|Formatting cache partition.");
            onOutputData?.Invoke("""{"DiskNumber":9,"BootDriveLetter":"S:","CacheDriveLetter":"T:"}""");

            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                ExitCode = 0,
                StandardOutput = provisioningOutput
            };
            Executions.Add(execution);
            return Task.FromResult(execution);
        }
    }

    private static string DecodePowerShellEncodedCommand(string arguments)
    {
        const string marker = "-EncodedCommand ";
        int markerIndex = arguments.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, arguments);

        string encodedCommand = arguments[(markerIndex + marker.Length)..].Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
    }

    private sealed class RecordingProgress : IProgress<WinPeMediaProgress>
    {
        public List<WinPeMediaProgress> Reports { get; } = [];

        public void Report(WinPeMediaProgress value)
        {
            Reports.Add(value);
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value)
        {
            Items.Add(value);
        }
    }

    private sealed class FakeRuntimePayloadProvisioningService(WinPeResult? result = null) : IWinPeRuntimePayloadProvisioningService
    {
        public List<WinPeRuntimePayloadProvisioningOptions> Options { get; } = [];
        public List<IProgress<WinPeDownloadProgress>?> DownloadProgress { get; } = [];

        public Task<WinPeResult> ProvisionAsync(
            WinPeRuntimePayloadProvisioningOptions options,
            IProgress<WinPeDownloadProgress>? downloadProgress = null,
            CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            DownloadProgress.Add(downloadProgress);
            return Task.FromResult(result ?? WinPeResult.Success());
        }
    }

    private static void CreateVerifiedBootPartition(string bootRootPath, WinPeArchitecture architecture)
    {
        Directory.CreateDirectory(Path.Combine(bootRootPath, "sources"));
        Directory.CreateDirectory(Path.Combine(bootRootPath, "boot"));
        Directory.CreateDirectory(Path.Combine(bootRootPath, "EFI", "Boot"));
        File.WriteAllText(Path.Combine(bootRootPath, "sources", "boot.wim"), "boot");
        File.WriteAllText(Path.Combine(bootRootPath, "boot", "BCD"), "bcd");
        File.WriteAllText(Path.Combine(bootRootPath, "EFI", "Boot", architecture.ToBootEfiName()), "efi");
    }

    private static void CopyRobocopyMedia(string arguments)
    {
        const string pathArgumentsPattern = """^(?:"(?<source>[^"]+)"|(?<source>\S+))\s+(?:"(?<destination>[^"]+)"|(?<destination>\S+))""";
        Match match = Regex.Match(arguments, pathArgumentsPattern, RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("Could not parse fake robocopy paths.");
        }

        string sourcePath = match.Groups["source"].Value;
        string destinationPath = match.Groups["destination"].Value;
        Directory.CreateDirectory(destinationPath);
        foreach (string directoryPath in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, directoryPath)));
        }

        foreach (string filePath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            string destinationFilePath = Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, filePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
            File.Copy(filePath, destinationFilePath, overwrite: true);
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        private TempWorkspace(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public static TempWorkspace Create()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"foundry-usb-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);
            return new TempWorkspace(rootPath);
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

}
