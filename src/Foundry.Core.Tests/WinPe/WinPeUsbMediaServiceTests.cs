// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foundry.Core.Services.WinPe;
using Foundry.Core.Tests.TestUtilities;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeUsbMediaServiceTests
{
    private const string BootVolumePath = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string CacheVolumePath = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";
    private static WinPeUsbDiskIdentity ConfirmedDisk => new()
    {
        Number = 9,
        FriendlyName = "Safe USB",
        SerialNumber = "SERIAL",
        UniqueId = "UNIQUE",
        BusType = "USB",
        IsRemovable = true,
        Size = 64000000000
    };

    [Theory]
    [InlineData(false, UsbFormatMode.Quick)]
    [InlineData(true, UsbFormatMode.Complete)]
    public async Task ProvisionAndPopulateAsync_RejectsTruncatedLayoutBeforeResolvingDestinations(bool truncateError, UsbFormatMode formatMode)
    {
        using var workspace = new TemporaryDirectory();
        var runner = new FakeSequenceRunner(JsonSerializer.Serialize(ConfirmedDisk), JsonSerializer.Serialize(TestLayout))
        {
            TruncateAtExecution = 1,
            TruncateError = truncateError
        };
        var service = new WinPeUsbMediaService(runner, _ => throw new InvalidOperationException("Must not resolve an incomplete layout."));

        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions { TargetDiskNumber = 9, ExpectedDisk = ConfirmedDisk, FormatMode = formatMode },
            new WinPeBuildArtifact { WorkingDirectoryPath = workspace.Path },
            new WinPeToolPaths { PowerShellPath = "must-not-run.exe" }, false, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbProvisioningFailed, result.Error?.Code);
        Assert.Equal(2, runner.Executions.Count);
        Assert.Equal(TimeSpan.FromMinutes(2), runner.ExecutionTimeouts[0]);
        Assert.Equal(TimeSpan.FromHours(formatMode == UsbFormatMode.Complete ? 24 : 4), runner.ExecutionTimeouts[1]);
        Assert.DoesNotContain("UNIQUE", result.Error?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Volume{", result.Error?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sources/boot.wim")]
    [InlineData("boot/BCD")]
    [InlineData("EFI/Boot/bootx64.efi")]
    [InlineData("Foundry")]
    public void UsbVerification_ReportsLogicalArtifactWithoutPhysicalRoot(string failingArtifact)
    {
        using var temporary = new TemporaryDirectory();
        CreateVerifiedBootPartition(temporary.Path, WinPeArchitecture.X64);
        if (failingArtifact == "Foundry")
        {
            Directory.CreateDirectory(Path.Combine(temporary.Path, failingArtifact));
        }
        else
        {
            File.Delete(Path.Combine(temporary.Path, failingArtifact));
        }
        WinPeResult result = failingArtifact == "Foundry"
            ? WinPeUsbMediaService.VerifyBootPartitionLayout(temporary.Path)
            : WinPeUsbMediaService.VerifyBootArtifacts(temporary.Path, WinPeArchitecture.X64);
        Assert.False(result.IsSuccess);
        Assert.Contains("BOOT/" + failingArtifact, result.Error?.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(temporary.Path, result.Error?.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsbPopulation_TranslatesFilesystemExceptionsWithoutDeviceDetails(bool update)
    {
        using var temporary = new TemporaryDirectory();
        string bootRoot = Path.Combine(temporary.Path, "boot");
        string cacheRoot = Path.Combine(temporary.Path, "Volume{22222222-2222-2222-2222-222222222222}");
        string mediaRoot = Path.Combine(temporary.Path, "media");
        CreateVerifiedBootPartition(mediaRoot, WinPeArchitecture.X64);
        File.WriteAllText(cacheRoot, "block-directory-creation");
        string layoutJson = JsonSerializer.Serialize(TestLayout);
        var outputs = new List<string> { JsonSerializer.Serialize(ConfirmedDisk), layoutJson };
        if (update) { outputs.Add(layoutJson); }
        outputs.AddRange([layoutJson, string.Empty, layoutJson]);
        var runner = new FakeSequenceRunner(true, 0, outputs.ToArray());
        string ResolveTestRoot(string volume) => volume switch
        {
            BootVolumePath => bootRoot,
            CacheVolumePath => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        var service = new WinPeUsbMediaService(runner, ResolveTestRoot);
        var options = new UsbOutputOptions
        {
            TargetDiskNumber = 9,
            ExpectedDisk = ConfirmedDisk,
            RuntimePayloadProvisioning = new WinPeRuntimePayloadProvisioningOptions()
        };
        var artifact = new WinPeBuildArtifact { WorkingDirectoryPath = temporary.Path, MediaDirectoryPath = mediaRoot, Architecture = WinPeArchitecture.X64 };
        var tools = new WinPeToolPaths { PowerShellPath = "pwsh.exe" };
        WinPeResult<WinPeUsbProvisionResult> result = update
            ? await service.UpdateBootPartitionAsync(options, artifact, tools, false, TestContext.Current.CancellationToken)
            : await service.ProvisionAndPopulateAsync(options, artifact, tools, false, TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeFailureKinds.FileSystem, result.Error?.FailureKind);
        Assert.Null(result.Error?.Exception);
        Assert.DoesNotContain("Volume{", result.Error?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(temporary.Path, result.Error?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("USB-123-extra", "USB-123")]
    [InlineData("usb-123", "USB-123")]
    public void ValidateDiskSafety_RejectsAChangedStableId(string actualId, string expectedId)
    {
        var actual = new WinPeUsbDiskIdentity
        {
            Number = 9,
            UniqueId = actualId,
            SerialNumber = "SERIAL",
            BusType = "USB",
            IsRemovable = true,
            Size = 64000000000
        };
        WinPeResult result = WinPeUsbMediaService.ValidateDiskSafety(
            new UsbOutputOptions { TargetDiskNumber = 9, ExpectedDisk = actual with { UniqueId = expectedId } }, actual, [actual]);
        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbIdentityMismatch, result.Error?.Code);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task UsbPopulation_StopsAtEachPopulationBoundaryWhenVolumeChanges(int changedPhase, bool update)
    {
        using var temporary = new TemporaryDirectory();
        string bootRoot = Path.Combine(temporary.Path, "boot");
        string cacheRoot = Path.Combine(temporary.Path, "cache");
        string mediaRoot = Path.Combine(temporary.Path, "media");
        CreateVerifiedBootPartition(mediaRoot, WinPeArchitecture.X64);
        Directory.CreateDirectory(Path.Combine(temporary.Path, "bootbins"));
        File.WriteAllText(Path.Combine(temporary.Path, "bootbins", "bootmgfw_EX.efi"), "replacement-boot-manager");
        WinPeUsbProvisionResult layout = TestLayout;
        string layoutJson = JsonSerializer.Serialize(layout);
        string changedJson = JsonSerializer.Serialize(layout with { CacheVolumeUniqueId = "reused-volume" });
        var outputs = new List<string> { JsonSerializer.Serialize(ConfirmedDisk), layoutJson };
        if (update) { outputs.Add(layoutJson); }
        for (int phase = 0; phase <= changedPhase; phase++)
        {
            outputs.Add(phase == changedPhase ? changedJson : layoutJson);
            if (phase == 0 && changedPhase > 0) { outputs.Add(string.Empty); }
        }
        var runner = new FakeSequenceRunner(true, 0, outputs.ToArray());
        var runtime = new FakeRuntimePayloadProvisioningService();
        string ResolveTestRoot(string volume) => volume switch
        {
            BootVolumePath => bootRoot,
            CacheVolumePath => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        var service = new WinPeUsbMediaService(runner, runtime, ResolveTestRoot);
        var options = new UsbOutputOptions
        {
            TargetDiskNumber = 9,
            ExpectedDisk = ConfirmedDisk,
            RuntimePayloadProvisioning = new WinPeRuntimePayloadProvisioningOptions()
        };
        var artifact = new WinPeBuildArtifact { WorkingDirectoryPath = temporary.Path, MediaDirectoryPath = mediaRoot, Architecture = WinPeArchitecture.X64 };
        var tools = new WinPeToolPaths { PowerShellPath = "pwsh.exe" };
        WinPeResult<WinPeUsbProvisionResult> result = update
            ? await service.UpdateBootPartitionAsync(options, artifact, tools, true, TestContext.Current.CancellationToken)
            : await service.ProvisionAndPopulateAsync(options, artifact, tools, true, TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbIdentityMismatch, result.Error?.Code);
        Assert.Empty(runtime.Options);
        Assert.Equal(changedPhase > 0, File.Exists(Path.Combine(bootRoot, "sources", "boot.wim")));
        Assert.Equal(changedPhase > 2, Directory.Exists(Path.Combine(cacheRoot, "Cache")));
        if (changedPhase == 1)
        {
            Assert.NotEqual("replacement-boot-manager", File.ReadAllText(Path.Combine(bootRoot, "EFI", "Boot", "bootx64.efi")));
        }
    }

    [Theory]
    [InlineData("letter")]
    [InlineData("missing")]
    [InlineData("same-volume")]
    public async Task ProvisionAndPopulateAsync_RejectsUnboundLayoutBeforeResolvingDestinations(string failure)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        WinPeUsbProvisionResult layout = failure switch
        {
            "letter" => TestLayout with { BootVolumePath = "Y:\\" },
            "missing" => TestLayout with { ConfirmedDisk = null },
            _ => TestLayout with { CacheVolumeUniqueId = TestLayout.BootVolumeUniqueId }
        };
        var runner = new FakeSequenceRunner(JsonSerializer.Serialize(ConfirmedDisk), JsonSerializer.Serialize(layout));
        var service = new WinPeUsbMediaService(runner, _ => throw new InvalidOperationException("Must not resolve an unbound destination."));
        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions { TargetDiskNumber = 9, ExpectedDisk = ConfirmedDisk },
            new WinPeBuildArtifact { WorkingDirectoryPath = workspace.RootPath },
            new WinPeToolPaths { PowerShellPath = "pwsh.exe" }, false, TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(WinPeErrorCodes.UsbProvisioningFailed, result.Error?.Code);
        Assert.Equal(2, runner.Executions.Count);
    }

    private static WinPeUsbProvisionResult TestLayout => new()
    {
        ConfirmedDisk = ConfirmedDisk,
        BootPartitionNumber = 1,
        CachePartitionNumber = 2,
        BootPartitionOffset = 1048576,
        CachePartitionOffset = 2148532224,
        BootPartitionSize = 2147483648,
        CachePartitionSize = 60000000000,
        BootVolumeUniqueId = "boot-id",
        CacheVolumeUniqueId = "cache-id",
        BootVolumePath = BootVolumePath,
        CacheVolumePath = CacheVolumePath,
        BootDriveLetter = "Y:",
        CacheDriveLetter = "Z:"
    };

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

    [Theory]
    [InlineData("number")]
    [InlineData("capacity")]
    [InlineData("bus")]
    [InlineData("system")]
    [InlineData("boot")]
    [InlineData("offline")]
    [InlineData("readonly")]
    [InlineData("fixed")]
    [InlineData("serial")]
    [InlineData("missing")]
    public void ValidateDiskSafety_RejectsChangedSnapshot(string change)
    {
        WinPeUsbDiskIdentity expected = ConfirmedDisk;
        WinPeUsbDiskIdentity actual = change switch
        {
            "number" => expected with { Number = 10 },
            "capacity" => expected with { Size = expected.Size + 1 },
            "bus" => expected with { BusType = "SATA" },
            "system" => expected with { IsSystem = true },
            "boot" => expected with { IsBoot = true },
            "offline" => expected with { IsOffline = true },
            "readonly" => expected with { IsReadOnly = true },
            "fixed" => expected with { IsRemovable = false },
            "serial" => expected with { SerialNumber = "SERIAL-extra" },
            _ => expected with { UniqueId = "", SerialNumber = "" }
        };
        Assert.False(WinPeUsbMediaService.ValidateDiskSafety(
            new UsbOutputOptions { TargetDiskNumber = 9, ExpectedDisk = expected }, actual, [actual]).IsSuccess);
    }

    [Fact]
    public void ValidateDiskSafety_RejectsMissingOrDuplicateStableIdentity()
    {
        foreach (WinPeUsbDiskIdentity expected in new[]
        {
            ConfirmedDisk,
            ConfirmedDisk with { UniqueId = "" },
            ConfirmedDisk with { UniqueId = "", SerialNumber = "" }
        })
        {
            WinPeResult result = WinPeUsbMediaService.ValidateDiskSafety(
                new UsbOutputOptions { TargetDiskNumber = 9, ExpectedDisk = expected }, expected,
                [expected, expected with { Number = 10 }]);
            Assert.False(result.IsSuccess);
            Assert.Equal(WinPeErrorCodes.UsbIdentityMismatch, result.Error?.Code);
        }
    }

    [Fact]
    public void ValidateDiskSafety_AcceptsPaddingAndUnknownRemovableWithoutUsingFriendlyName()
    {
        WinPeUsbDiskIdentity actual = ConfirmedDisk with { UniqueId = " UNIQUE ", SerialNumber = " SERIAL ", FriendlyName = "Renamed", IsRemovable = null };
        Assert.True(WinPeUsbMediaService.ValidateDiskSafety(
            new UsbOutputOptions { TargetDiskNumber = 9, ExpectedDisk = ConfirmedDisk }, actual, [actual]).IsSuccess);
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
                ExpectedDisk = ConfirmedDisk
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
                                    {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"Y:","CacheDriveLetter":"Z:"}
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
            BootVolumePath => bootRoot,
            CacheVolumePath => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("S:\\"));
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 0,
            diskIdentity,
            provisioningResult,
            provisioningResult,
            string.Empty,
            provisioningResult);
        var service = new WinPeUsbMediaService(runner, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDisk = ConfirmedDisk,
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
        Assert.Equal(4, runner.Executions.Count(execution => execution.FileName == "pwsh.exe"));
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
                                    {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"Y:","CacheDriveLetter":"Z:"}
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
            BootVolumePath => bootRoot,
            CacheVolumePath => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("S:\\"));
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 1,
            diskIdentity,
            provisioningResult,
            provisioningResult,
            string.Empty,
            provisioningResult);
        var service = new WinPeUsbMediaService(runner, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.ProvisionAndPopulateAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDisk = ConfirmedDisk,
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
                                    {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"S:","CacheDriveLetter":"T:"}
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
            BootVolumePath => bootRoot,
            CacheVolumePath => cacheRoot,
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
                ExpectedDisk = ConfirmedDisk,
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
                        {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                        """;
        string formatResult = """
                              FOUNDRY_USB_PROGRESS|35|Formatting BOOT partition.
                              {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"S:","CacheDriveLetter":"T:"}
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
            BootVolumePath => bootRoot,
            CacheVolumePath => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("Y:\\"));
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 0,
            diskIdentity,
            layout,
            formatResult,
            formatResult,
            string.Empty,
            formatResult,
            formatResult);
        var progress = new RecordingProgress();
        var service = new WinPeUsbMediaService(runner, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.UpdateBootPartitionAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDisk = ConfirmedDisk,
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
        Assert.Equal(4, runner.Executions.Count(execution => execution.FileName == "pwsh.exe"));
        Assert.Contains(runner.Executions, execution => execution.FileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(runner.Executions, execution => execution.Arguments.Contains("Clear-Disk", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Executions, execution => execution.Arguments.Contains("Foundry Cache", StringComparison.Ordinal));
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
            BootVolumePath => bootRoot,
            CacheVolumePath => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("Y:\\"));
        string layout = """
                        {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                        """;
        string formatResult = """
                              FOUNDRY_USB_PROGRESS|35|Formatting BOOT partition.
                              {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                              """;
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 0,
            diskIdentity,
            layout,
            formatResult,
            formatResult,
            string.Empty,
            formatResult,
            formatResult);
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
                ExpectedDisk = ConfirmedDisk,
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
            BootVolumePath => bootRoot,
            CacheVolumePath => cacheRoot,
            _ => throw new InvalidOperationException("Unowned test volume.")
        };
        Assert.Throws<InvalidOperationException>(() => ResolveTestRoot("Y:\\"));
        string layout = """
                        {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                        """;
        string formatResult = """
                              FOUNDRY_USB_PROGRESS|35|Formatting BOOT partition.
                              {"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"S:","CacheDriveLetter":"T:"}
                              """;
        var runner = new FakeSequenceRunner(
            copyMedia: true,
            robocopyExitCode: 0,
            diskIdentity,
            layout,
            formatResult,
            formatResult,
            string.Empty,
            formatResult,
            formatResult);
        var runtimeProvisioningService = new FakeRuntimePayloadProvisioningService(
            WinPeResult.Failure(
                WinPeErrorCodes.BuildFailed,
                "Failed to provision Foundry runtime payloads.",
                CacheVolumePath + "Runtime/Foundry.Connect.exe"));
        var progress = new RecordingProgress();
        var service = new WinPeUsbMediaService(runner, runtimeProvisioningService, ResolveTestRoot);

        WinPeResult<WinPeUsbProvisionResult> result = await service.UpdateBootPartitionAsync(
            new UsbOutputOptions
            {
                TargetDiskNumber = 9,
                ExpectedDisk = ConfirmedDisk,
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
        Assert.Null(result.Error?.Exception);
        Assert.DoesNotContain("Volume{", result.Error?.ToString(), StringComparison.Ordinal);
        Assert.Contains("CACHE/Runtime", result.Error?.Details, StringComparison.Ordinal);
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
                ExpectedDisk = ConfirmedDisk
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
        int robocopyExitCode = 0,
        string? layoutOutput = null) : IWinPeProcessRunner
    {
        protected string? LayoutOutput { get; } = layoutOutput;
        public List<WinPeProcessExecution> Executions { get; } = [];

        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException("Executable calls must pass argument tokens.");
        }

        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            IReadOnlyList<string> argumentList,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            string arguments = string.Join(' ', argumentList);
            bool isRobocopy = fileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase);
            if (isRobocopy && copyMedia && robocopyExitCode < 8)
            {
                CopyRobocopyMedia(argumentList);
            }

            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                ExitCode = isRobocopy ? robocopyExitCode : 0,
                StandardOutput = !isRobocopy && arguments.Length > 0 && DecodePowerShellEncodedCommand(arguments).Contains("$layout =", StringComparison.Ordinal) ? LayoutOutput ?? output : output
            };
            Executions.Add(execution);
            return Task.FromResult(execution);
        }

        public Task<WinPeProcessExecution> RunCmdScriptAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
        }

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSequenceRunner : IWinPeProcessRunner
    {
        public int? TruncateAtExecution { get; init; }
        public bool TruncateError { get; init; }
        public List<TimeSpan?> ExecutionTimeouts { get; } = [];
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
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException("Executable calls must pass argument tokens.");
        }

        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            IReadOnlyList<string> argumentList,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            string arguments = string.Join(' ', argumentList);
            bool isRobocopy = fileName.EndsWith("robocopy.exe", StringComparison.OrdinalIgnoreCase);
            if (isRobocopy && _copyMedia && _robocopyExitCode < 8)
            {
                CopyRobocopyMedia(argumentList);
            }

            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                ExitCode = isRobocopy ? _robocopyExitCode : 0,
                StandardOutput = _outputs.Count > 0 ? _outputs.Dequeue() : string.Empty,
                StandardOutputTruncated = TruncateAtExecution == Executions.Count && !TruncateError,
                StandardErrorTruncated = TruncateAtExecution == Executions.Count && TruncateError
            };
            ExecutionTimeouts.Add(executionTimeout);
            Executions.Add(execution);
            return Task.FromResult(execution);
        }

        public Task<WinPeProcessExecution> RunCmdScriptAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
        }

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeOutputRunner(string output, string provisioningOutput)
        : FakeRunner(output, copyMedia: true, layoutOutput: provisioningOutput), IWinPeProcessOutputRunner
    {
        public Task<WinPeProcessExecution> RunWithOutputAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            Action<string>? onOutputData,
            Action<string>? onErrorData,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException("Executable calls must pass argument tokens.");
        }

        public Task<WinPeProcessExecution> RunWithOutputAsync(
            string fileName,
            IReadOnlyList<string> argumentList,
            string workingDirectory,
            Action<string>? onOutputData,
            Action<string>? onErrorData,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            string arguments = string.Join(' ', argumentList);
            onOutputData?.Invoke("FOUNDRY_USB_PROGRESS|26|Clearing USB partition table.");
            onOutputData?.Invoke("FOUNDRY_USB_PROGRESS|44|Formatting BOOT partition.");
            onOutputData?.Invoke("FOUNDRY_USB_VERBOSE|BOOT partition formatted. DriveLetter=S, FileSystem=FAT32, Label=BOOT.");
            onOutputData?.Invoke("FOUNDRY_USB_PROGRESS|53|Formatting cache partition.");
            onOutputData?.Invoke("""{"ConfirmedDisk":{"Number":9,"FriendlyName":"Safe USB","SerialNumber":"SERIAL","UniqueId":"UNIQUE","BusType":"USB","IsRemovable":true,"Size":64000000000},"BootPartitionNumber":1,"CachePartitionNumber":2,"BootPartitionOffset":1048576,"CachePartitionOffset":2148532224,"BootPartitionSize":2147483648,"CachePartitionSize":60000000000,"BootVolumeUniqueId":"boot-id","CacheVolumeUniqueId":"cache-id","BootVolumePath":"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\","CacheVolumePath":"\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\","BootDriveLetter":"S:","CacheDriveLetter":"T:"}""");

            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                ExitCode = 0,
                StandardOutput = LayoutOutput!
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
        Assert.True(arguments.Length < 32000, "Encoded USB command exceeds the Windows command-line limit.");

        string encodedCommand = arguments[(markerIndex + marker.Length)..].Trim();
        string loader = Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
        Match payload = Regex.Match(loader, @"FromBase64String\('([^']+)'\)");
        Assert.True(payload.Success);
        using var stream = new MemoryStream(Convert.FromBase64String(payload.Groups[1].Value));
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
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

    private static void CopyRobocopyMedia(IReadOnlyList<string> arguments)
    {
        string sourcePath = arguments[0];
        string destinationPath = arguments[1];
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
