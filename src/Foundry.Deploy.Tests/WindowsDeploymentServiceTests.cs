// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Foundry.Core.Models.Configuration;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Autopilot;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Security;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class WindowsDeploymentServiceTests
{
    [Fact]
    public async Task ConfigureBootAsync_RejectsMissingRetainedLayout()
    {
        var runner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(runner, NullLogger<WindowsDeploymentService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureBootAsync(@"W:\", @"S:\", 26200,
            Path.GetTempPath(), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }
    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_WhenDisabled_DoesNotRunDism()
    {
        using var workspace = new TemporaryWorkspace();
        var processRunner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        WindowsOptionalFeatureServicingResult result = await service.ConfigureOfflineWindowsOptionalFeaturesAsync(
            Path.Combine(workspace.RootPath, "setup.esd"),
            workspace.RootPath,
            1,
            new DeployWindowsOptionalFeatureSettings(),
            Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures"),
            Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia"),
            Path.Combine(workspace.RootPath, "Temp", "Deployment"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.RequestedActionCount);
        Assert.Empty(processRunner.Calls);
    }

    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_OrdersChangesAndUsesOfflineArguments()
    {
        using var workspace = new TemporaryWorkspace();
        int inspectionCount = 0;
        var processRunner = new RecordingProcessRunner
        {
            ResultFactory = arguments => arguments.Contains("/Get-Features", StringComparison.OrdinalIgnoreCase)
                ? new ProcessExecutionResult
                {
                    ExitCode = 0,
                    StandardOutput = ++inspectionCount == 1
                        ? "Microsoft-Hyper-V-All | Disabled\nMicrosoft-Hyper-V | Disabled\nTelnetClient | Enabled"
                        : "Microsoft-Hyper-V-All | Enabled\nMicrosoft-Hyper-V | Enable Pending\nTelnetClient | Disable Pending"
                }
                : new ProcessExecutionResult { ExitCode = 0 }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        WindowsOptionalFeatureServicingResult result = await service.ConfigureOfflineWindowsOptionalFeaturesAsync(
            Path.Combine(workspace.RootPath, "setup.esd"),
            workspace.RootPath,
            1,
            new DeployWindowsOptionalFeatureSettings
            {
                IsEnabled = true,
                Actions =
                [
                    new() { Id = "wf:microsoft-hyper-v", Enable = true },
                    new() { Id = "wf:telnetclient", Enable = false },
                    new() { Id = "wf:microsoft-hyper-v-all", Enable = true }
                ]
            },
            Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures"),
            Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia"),
            Path.Combine(workspace.RootPath, "Temp", "Deployment"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.RequestedActionCount);
        Assert.Equal(3, result.ChangedActionCount);
        Assert.Equal(0, result.AlreadySatisfiedActionCount);
        string[] servicingCalls = processRunner.Calls
            .Where(call =>
                call.Contains("/Enable-Feature", StringComparison.OrdinalIgnoreCase) ||
                call.Contains("/Disable-Feature", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Contains("/FeatureName:Microsoft-Hyper-V-All", servicingCalls[0], StringComparison.Ordinal);
        Assert.Contains("/FeatureName:Microsoft-Hyper-V", servicingCalls[1], StringComparison.Ordinal);
        Assert.Contains("/FeatureName:TelnetClient", servicingCalls[2], StringComparison.Ordinal);
        Assert.Contains("/LimitAccess", servicingCalls[0], StringComparison.Ordinal);
        Assert.Contains("/LimitAccess", servicingCalls[1], StringComparison.Ordinal);
        Assert.DoesNotContain("/LimitAccess", servicingCalls[2], StringComparison.Ordinal);
        Assert.DoesNotContain("/Remove", servicingCalls[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_TracksAbsentEnableAndMissingDisable()
    {
        using var workspace = new TemporaryWorkspace();
        var processRunner = new RecordingProcessRunner
        {
            Result = new ProcessExecutionResult { ExitCode = 0, StandardOutput = "NetFx4-AdvSrvs | Enabled" }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        WindowsOptionalFeatureServicingResult result = await service.ConfigureOfflineWindowsOptionalFeaturesAsync(
            Path.Combine(workspace.RootPath, "setup.esd"),
            workspace.RootPath,
            1,
            new DeployWindowsOptionalFeatureSettings
            {
                IsEnabled = true,
                Actions =
                [
                    new() { Id = "wf:netfx4-advsrvs", Enable = true },
                    new() { Id = "wf:telnetclient", Enable = false },
                    new() { Id = "wf:recall", Enable = true }
                ]
            },
            Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures"),
            Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia"),
            Path.Combine(workspace.RootPath, "Temp", "Deployment"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.AlreadySatisfiedActionCount);
        Assert.Equal(["wf:recall"], result.UnavailableEnableActionIds);
        Assert.Equal(0, result.ChangedActionCount);
    }

    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_WhenDismOutputCannotBeParsed_FailsClosed()
    {
        using var workspace = new TemporaryWorkspace();
        var processRunner = new RecordingProcessRunner
        {
            Result = new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "Feature Name State"
            }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfigureOfflineWindowsOptionalFeaturesAsync(
                Path.Combine(workspace.RootPath, "setup.esd"),
                workspace.RootPath,
                1,
                new DeployWindowsOptionalFeatureSettings
                {
                    IsEnabled = true,
                    Actions = [new() { Id = "wf:telnetclient", Enable = true }]
                },
                Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures"),
                Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia"),
                Path.Combine(workspace.RootPath, "Temp", "Deployment"),
                TestContext.Current.CancellationToken));

        Assert.Contains("parse", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(processRunner.Calls, call => call.Contains("/Enable-Feature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_WhenCleanupBoundaryIsDriveRoot_RejectsInput()
    {
        using var workspace = new TemporaryWorkspace();
        var processRunner = new RecordingProcessRunner
        {
            Result = new ProcessExecutionResult { ExitCode = 0, StandardOutput = "TelnetClient | Enabled" }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ConfigureOfflineWindowsOptionalFeaturesAsync(
                Path.Combine(workspace.RootPath, "setup.esd"),
                workspace.RootPath,
                1,
                new DeployWindowsOptionalFeatureSettings
                {
                    IsEnabled = true,
                    Actions = [new() { Id = "wf:telnetclient", Enable = true }]
                },
                Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures"),
                Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia"),
                windowsDirectory,
                TestContext.Current.CancellationToken));

        Assert.Empty(processRunner.Calls);
    }

    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_WhenSourceIsRequired_UsesAppliedImageMetadata()
    {
        using var workspace = new TemporaryWorkspace();
        string imagePath = Path.Combine(workspace.RootPath, "setup.esd");
        await File.WriteAllTextAsync(imagePath, string.Empty, TestContext.Current.CancellationToken);
        string scratchDirectory = Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures");
        string sourceDirectory = Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia");
        int inspectionCount = 0;
        bool applyDirectoryExisted = false;
        var processRunner = new RecordingProcessRunner
        {
            ResultFactory = arguments =>
            {
                if (arguments.Contains("/Get-Features", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = ++inspectionCount == 1
                            ? "NetFx3 | Disabled with Payload Removed"
                            : "NetFx3 | Enabled"
                    };
                }

                if (arguments.Contains("/Get-ImageInfo", StringComparison.OrdinalIgnoreCase) &&
                    arguments.Contains("/Index:3", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = "Index : 3\nName : Windows Setup Media\nArchitecture : <undefined>\nVersion : <undefined>"
                    };
                }

                if (arguments.Contains("/Get-ImageInfo", StringComparison.OrdinalIgnoreCase) &&
                    arguments.Contains("/Index:9", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = "Index : 9\nName : Windows 11 Enterprise\nArchitecture : x64\nVersion : 10.0.26200"
                    };
                }

                if (arguments.Contains("/Get-ImageInfo", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = "Index : 3\nName : Windows Setup Media\n\nIndex : 9\nName : Windows 11 Enterprise"
                    };
                }

                if (arguments.Contains("/Apply-Image", StringComparison.OrdinalIgnoreCase))
                {
                    applyDirectoryExisted = Directory.Exists(sourceDirectory);
                    string sxsDirectory = Path.Combine(sourceDirectory, "sources", "sxs");
                    Directory.CreateDirectory(sxsDirectory);
                    File.WriteAllText(
                        Path.Combine(sxsDirectory, "microsoft-windows-netfx3-ondemand-package~31bf3856ad364e35~amd64~~.cab"),
                        string.Empty);
                }

                return new ProcessExecutionResult { ExitCode = 0 };
            }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        WindowsOptionalFeatureServicingResult result = await service.ConfigureOfflineWindowsOptionalFeaturesAsync(
            imagePath,
            workspace.RootPath,
            9,
            new DeployWindowsOptionalFeatureSettings
            {
                IsEnabled = true,
                Actions = [new() { Id = "wf:netfx3", Enable = true }]
            },
            scratchDirectory,
            sourceDirectory,
            Path.Combine(workspace.RootPath, "Temp", "Deployment"),
            TestContext.Current.CancellationToken);

        Assert.True(result.MatchingSourceUsed);
        Assert.True(applyDirectoryExisted);
        Assert.Contains(processRunner.Calls, call => call.Contains("/Get-ImageInfo", StringComparison.OrdinalIgnoreCase) && call.Contains("/Index:9", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(processRunner.Calls, call => call.Contains("/Get-ImageInfo", StringComparison.OrdinalIgnoreCase) && call.Contains("/Index:3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(processRunner.Calls, call => call.Contains("/Apply-Image", StringComparison.OrdinalIgnoreCase) && call.Contains("/Index:3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(processRunner.Calls, call => call.Contains("/Enable-Feature", StringComparison.OrdinalIgnoreCase) && call.Contains($"/Source:{Path.Combine(sourceDirectory, "sources", "sxs")}", StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(scratchDirectory));
        Assert.False(Directory.Exists(sourceDirectory));
    }

    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_WhenSourceArchitectureDoesNotMatchAppliedImage_FailsBeforeServicing()
    {
        using var workspace = new TemporaryWorkspace();
        string imagePath = Path.Combine(workspace.RootPath, "setup.esd");
        await File.WriteAllTextAsync(imagePath, string.Empty, TestContext.Current.CancellationToken);
        string sourceDirectory = Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia");
        var processRunner = new RecordingProcessRunner
        {
            ResultFactory = arguments =>
            {
                if (arguments.Contains("/Get-Features", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = "NetFx3 | Disabled with Payload Removed"
                    };
                }

                if (arguments.Contains("/Get-ImageInfo", StringComparison.OrdinalIgnoreCase) &&
                    arguments.Contains("/Index:9", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = "Index : 9\nName : Windows 11 Enterprise\nArchitecture : ARM64\nVersion : 10.0.26200"
                    };
                }

                if (arguments.Contains("/Get-ImageInfo", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = "Index : 3\nName : Windows Setup Media\n\nIndex : 9\nName : Windows 11 Enterprise"
                    };
                }

                if (arguments.Contains("/Apply-Image", StringComparison.OrdinalIgnoreCase))
                {
                    string sxsDirectory = Path.Combine(sourceDirectory, "sources", "sxs");
                    Directory.CreateDirectory(sxsDirectory);
                    File.WriteAllText(
                        Path.Combine(sxsDirectory, "microsoft-windows-netfx3-ondemand-package~31bf3856ad364e35~amd64~~.cab"),
                        string.Empty);
                }

                return new ProcessExecutionResult { ExitCode = 0 };
            }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfigureOfflineWindowsOptionalFeaturesAsync(
                imagePath,
                workspace.RootPath,
                9,
                new DeployWindowsOptionalFeatureSettings
                {
                    IsEnabled = true,
                    Actions = [new() { Id = "wf:netfx3", Enable = true }]
                },
                Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures"),
                sourceDirectory,
                Path.Combine(workspace.RootPath, "Temp", "Deployment"),
                TestContext.Current.CancellationToken));

        Assert.Contains("arm64", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(processRunner.Calls, call => call.Contains("/Enable-Feature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_WhenNonSourcePayloadIsRemoved_FailsBeforeServicing()
    {
        using var workspace = new TemporaryWorkspace();
        var processRunner = new RecordingProcessRunner
        {
            Result = new ProcessExecutionResult { ExitCode = 0, StandardOutput = "TelnetClient | Disabled with Payload Removed" }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfigureOfflineWindowsOptionalFeaturesAsync(
                Path.Combine(workspace.RootPath, "setup.esd"),
                workspace.RootPath,
                1,
                new DeployWindowsOptionalFeatureSettings
                {
                    IsEnabled = true,
                    Actions = [new() { Id = "wf:telnetclient", Enable = true }]
                },
                Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures"),
                Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia"),
                Path.Combine(workspace.RootPath, "Temp", "Deployment"),
                TestContext.Current.CancellationToken));

        Assert.Contains("payload", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(processRunner.Calls, call => call.Contains("/Enable-Feature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfigureOfflineWindowsOptionalFeaturesAsync_WhenActionsConflict_RejectsInput()
    {
        using var workspace = new TemporaryWorkspace();
        var processRunner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfigureOfflineWindowsOptionalFeaturesAsync(
                Path.Combine(workspace.RootPath, "setup.esd"),
                workspace.RootPath,
                1,
                new DeployWindowsOptionalFeatureSettings
                {
                    IsEnabled = true,
                    Actions =
                    [
                        new() { Id = "wf:netfx3", Enable = true },
                        new() { Id = "wf:netfx3", Enable = false }
                    ]
                },
                Path.Combine(workspace.RootPath, "Temp", "Dism", "OptionalFeatures"),
                Path.Combine(workspace.RootPath, "Temp", "WindowsSetupMedia"),
                Path.Combine(workspace.RootPath, "Temp", "Deployment"),
                TestContext.Current.CancellationToken));

        Assert.Empty(processRunner.Calls);
    }

    [Fact]
    public async Task ResolveImageIndexAsync_WhenRequestedEditionIsMissing_ThrowsBeforeImageApplication()
    {
        using var workspace = new TemporaryWorkspace();
        string imagePath = Path.Combine(workspace.RootPath, "consumer.esd");
        await File.WriteAllTextAsync(imagePath, string.Empty, TestContext.Current.CancellationToken);
        var processRunner = new RecordingProcessRunner
        {
            ResultFactory = arguments => arguments.Contains("/Index:4", StringComparison.OrdinalIgnoreCase)
                ? new ProcessExecutionResult { ExitCode = 0, StandardOutput = "Index : 4\nEdition : Core" }
                : arguments.Contains("/Index:9", StringComparison.OrdinalIgnoreCase)
                    ? new ProcessExecutionResult { ExitCode = 0, StandardOutput = "Index : 9\nEdition : Professional" }
                    : new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = """
                    Index : 1
                    Name : Windows Setup Media

                    Index : 4
                    Name : Windows 11 Home

                    Index : 9
                    Name : Windows 11 Pro
                    """
                    }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveImageIndexAsync(
                imagePath,
                "Enterprise",
                workspace.RootPath,
                TestContext.Current.CancellationToken));

        Assert.Contains("Enterprise", exception.Message, StringComparison.Ordinal);
        Assert.Contains("4: Core", exception.Message, StringComparison.Ordinal);
        Assert.Contains("9: Professional", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveImageIndexAsync_WhenSingleImageDoesNotMatchRequestedEdition_Throws()
    {
        using var workspace = new TemporaryWorkspace();
        string imagePath = Path.Combine(workspace.RootPath, "setup-media.esd");
        await File.WriteAllTextAsync(imagePath, string.Empty, TestContext.Current.CancellationToken);
        var processRunner = new RecordingProcessRunner
        {
            Result = new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = """
                    Index : 1
                    Name : Windows Setup Media
                    """
            }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveImageIndexAsync(
                imagePath,
                "Enterprise",
                workspace.RootPath,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveImageIndexAsync_DoesNotSelectNVariantForNonNEdition()
    {
        using var workspace = new TemporaryWorkspace();
        string imagePath = Path.Combine(workspace.RootPath, "consumer.esd");
        await File.WriteAllTextAsync(imagePath, string.Empty, TestContext.Current.CancellationToken);
        var processRunner = new RecordingProcessRunner
        {
            ResultFactory = arguments => arguments.Contains("/Index:5", StringComparison.OrdinalIgnoreCase)
                ? new ProcessExecutionResult { ExitCode = 0, StandardOutput = "Index : 5\nEdition : ProfessionalN" }
                : arguments.Contains("/Index:9", StringComparison.OrdinalIgnoreCase)
                    ? new ProcessExecutionResult { ExitCode = 0, StandardOutput = "Index : 9\nEdition : Professional" }
                    : new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = """
                    Index : 5
                    Name : Windows 11 Pro N

                    Index : 9
                    Name : Windows 11 Pro
                    """
                    }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        int imageIndex = await service.ResolveImageIndexAsync(
            imagePath,
            "Pro",
            workspace.RootPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(9, imageIndex);
    }

    [Theory]
    [InlineData("Home", "Core", 4)]
    [InlineData("Home N", "CoreN", 5)]
    [InlineData("Home Single Language", "CoreSingleLanguage", 6)]
    [InlineData("Home China", "CoreCountrySpecific", 7)]
    [InlineData("Education", "Education", 8)]
    [InlineData("Education N", "EducationN", 9)]
    [InlineData("Pro", "Professional", 10)]
    [InlineData("Pro N", "ProfessionalN", 11)]
    [InlineData("Enterprise", "Enterprise", 12)]
    [InlineData("Enterprise N", "EnterpriseN", 13)]
    public async Task ResolveImageIndexAsync_ResolvesExactEditionIdFromDetailedImageMetadata(
        string edition,
        string editionId,
        int expectedIndex)
    {
        using var workspace = new TemporaryWorkspace();
        string imagePath = Path.Combine(workspace.RootPath, "windows.esd");
        await File.WriteAllTextAsync(imagePath, string.Empty, TestContext.Current.CancellationToken);
        var processRunner = new RecordingProcessRunner
        {
            ResultFactory = arguments => arguments.Contains($"/Index:{expectedIndex}", StringComparison.OrdinalIgnoreCase)
                ? new ProcessExecutionResult
                {
                    ExitCode = 0,
                    StandardOutput = $"""
                        Index : {expectedIndex}
                        Name : Nom Windows localise arbitraire
                        Edition : {editionId}
                        """
                }
                : arguments.Contains("/Index:", StringComparison.OrdinalIgnoreCase)
                    ? new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = """
                            Index : 1
                            Name : Windows Setup Media
                            """
                    }
                    : new ProcessExecutionResult
                    {
                        ExitCode = 0,
                        StandardOutput = $"""
                        Index : 1
                        Name : Windows Setup Media

                        Index : {expectedIndex}
                        Name : Nom Windows localise arbitraire
                        """
                    }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        int imageIndex = await service.ResolveImageIndexAsync(
            imagePath,
            edition,
            workspace.RootPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedIndex, imageIndex);
        Assert.Contains(processRunner.Calls, call => call.Contains($"/Index:{expectedIndex}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveImageIndexAsync_WhenEditionIdOccursMoreThanOnce_ThrowsWithoutFallback()
    {
        using var workspace = new TemporaryWorkspace();
        string imagePath = Path.Combine(workspace.RootPath, "windows.esd");
        await File.WriteAllTextAsync(imagePath, string.Empty, TestContext.Current.CancellationToken);
        var processRunner = new RecordingProcessRunner
        {
            ResultFactory = arguments => arguments.Contains("/Index:", StringComparison.OrdinalIgnoreCase)
                ? new ProcessExecutionResult { ExitCode = 0, StandardOutput = $"Index : {ParseRequestedIndex(arguments)}\nEdition : Professional" }
                : new ProcessExecutionResult { ExitCode = 0, StandardOutput = "Index : 8\nName : Pro first\n\nIndex : 9\nName : Pro second" }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveImageIndexAsync(
                imagePath,
                "Pro",
                workspace.RootPath,
                TestContext.Current.CancellationToken));

        Assert.Contains("found 2", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(26199, "/c /v")]
    [InlineData(26200, "/c /bootex /v")]
    public async Task ConfigureBootAsync_UsesAppliedWindowsBcdBootWithExpectedArguments(
        int operatingSystemBuildMajor,
        string expectedArguments)
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = Path.Combine(workspace.RootPath, "Target Windows");
        string windowsPath = Path.Combine(windowsRoot, "Windows");
        string bcdBootPath = Path.Combine(windowsPath, "System32", "bcdboot.exe");
        string workingDirectory = Path.Combine(workspace.RootPath, "Work");
        const string systemRoot = @"S:\";
        Directory.CreateDirectory(Path.GetDirectoryName(bcdBootPath)!);
        await File.WriteAllTextAsync(bcdBootPath, string.Empty, TestContext.Current.CancellationToken);
        var processRunner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        await service.RunBcdBootAsync(
            windowsRoot,
            systemRoot,
            operatingSystemBuildMajor,
            workingDirectory,
            TestContext.Current.CancellationToken);

        Assert.Equal(bcdBootPath, processRunner.LastFileName);
        Assert.Equal(
            new[] { windowsPath, "/s", "S:", "/f", "UEFI" }.Concat(expectedArguments.Split(' ')),
            processRunner.LastArgumentTokens);
        Assert.Equal(workingDirectory, processRunner.LastWorkingDirectory);
    }

    [Fact]
    public async Task ConfigureBootAsync_PassesSeparateTokensToRealChild()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = Path.Combine(workspace.RootPath, "Target Windows");
        string bcdBootPath = Path.Combine(windowsRoot, "Windows", "System32", "bcdboot.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bcdBootPath)!);
        await File.WriteAllTextAsync(bcdBootPath, string.Empty, TestContext.Current.CancellationToken);
        var runner = new ArgumentChildRunner();
        var service = new WindowsDeploymentService(runner, NullLogger<WindowsDeploymentService>.Instance);
        await service.RunBcdBootAsync(windowsRoot, @"S:\", 26200, workspace.RootPath, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { Path.Combine(windowsRoot, "Windows"), "/s", "S:", "/f", "UEFI", "/c", "/bootex", "/v" }, runner.Received);
    }

    private sealed class ArgumentChildRunner : IProcessRunner
    {
        public string[]? Received { get; private set; }
        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("BCDBoot must use separate tokens.");
        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
            => RunAsync(fileName, arguments, workingDirectory, null, null, cancellationToken);
        public async Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory,
            Action<string>? onOutputData, Action<string>? onErrorData, CancellationToken cancellationToken = default)
        {
            ProcessExecutionResult result = await new Foundry.Utilities.Processes.ProcessRunner().RunAsync(new ProcessExecutionRequest(
                Path.Combine(AppContext.BaseDirectory, "ProcessTestChild", "ProcessTestChild.exe"), ["argv", .. arguments], workingDirectory), cancellationToken);
            Received = System.Text.Json.JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim());
            return result;
        }
    }
    [Fact]
    public async Task ConfigureBootAsync_WhenAppliedBcdBootIsMissing_ThrowsFileNotFoundException()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = Path.Combine(workspace.RootPath, "WindowsRoot");
        string expectedBcdBootPath = Path.Combine(windowsRoot, "Windows", "System32", "bcdboot.exe");
        var processRunner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.RunBcdBootAsync(
                windowsRoot,
                @"S:\",
                26200,
                Path.Combine(workspace.RootPath, "Work"),
                TestContext.Current.CancellationToken));

        Assert.Equal(expectedBcdBootPath, exception.FileName);
        Assert.Null(processRunner.LastFileName);
    }

    [Fact]
    public async Task ConfigureBootAsync_WhenAppliedBcdBootFails_PropagatesDiagnostic()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = Path.Combine(workspace.RootPath, "WindowsRoot");
        string bcdBootPath = Path.Combine(windowsRoot, "Windows", "System32", "bcdboot.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bcdBootPath)!);
        await File.WriteAllTextAsync(bcdBootPath, string.Empty, TestContext.Current.CancellationToken);
        var processRunner = new RecordingProcessRunner
        {
            Result = new ProcessExecutionResult
            {
                ExitCode = 193,
                StandardOutput = "Failure when attempting to copy boot files.",
                StandardError = "diagnostic"
            }
        };
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        DeploymentProcessException exception = await Assert.ThrowsAsync<DeploymentProcessException>(() =>
            service.RunBcdBootAsync(
                windowsRoot,
                @"S:\",
                26200,
                Path.Combine(workspace.RootPath, "Work"),
                TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal(bcdBootPath, processRunner.LastFileName);
        Assert.Contains("BCDBoot configuration failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ExitCode: 193", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failure when attempting to copy boot files.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureOfflineComputerNameAsync_WhenDefaultTimeZoneIdIsProvided_WritesUnattendTimeZone()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = Path.Combine(workspace.RootPath, "WindowsRoot");
        Directory.CreateDirectory(windowsRoot);

        var service = new WindowsDeploymentService(new NoOpProcessRunner(), NullLogger<WindowsDeploymentService>.Instance);

        await service.ConfigureOfflineComputerNameAsync(
            windowsRoot,
            "LAB01",
            "amd64",
            "Romance Standard Time");

        string unattendPath = Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml");
        XDocument document = XDocument.Load(unattendPath);
        XNamespace ns = "urn:schemas-microsoft-com:unattend";

        Assert.Equal("LAB01", document.Descendants(ns + "ComputerName").Single().Value);
        Assert.Equal("Romance Standard Time", document.Descendants(ns + "TimeZone").Single().Value);
    }

    [Fact]
    public async Task ConfigureOfflineComputerNameAsync_WhenIanaTimeZoneIdIsProvided_WritesWindowsTimeZoneId()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = Path.Combine(workspace.RootPath, "WindowsRoot");
        Directory.CreateDirectory(windowsRoot);

        var service = new WindowsDeploymentService(new NoOpProcessRunner(), NullLogger<WindowsDeploymentService>.Instance);

        await service.ConfigureOfflineComputerNameAsync(
            windowsRoot,
            "LAB01",
            "amd64",
            "Europe/Paris");

        string unattendPath = Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml");
        XDocument document = XDocument.Load(unattendPath);
        XNamespace ns = "urn:schemas-microsoft-com:unattend";

        Assert.Equal("Romance Standard Time", document.Descendants(ns + "TimeZone").Single().Value);
    }

    [Fact]
    public async Task ConfigureOfflineOobeAsync_WhenEnabled_WritesUnattendAndPrivacyPolicies()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = CreateWindowsRoot(workspace);
        string workingDirectory = Path.Combine(workspace.RootPath, "Work");
        var processRunner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        await service.ConfigureOfflineOobeAsync(
            windowsRoot,
            new DeployOobeSettings
            {
                IsEnabled = true,
                SkipLicenseTerms = true,
                DiagnosticDataLevel = DeployOobeDiagnosticDataLevel.Off,
                HidePrivacySetup = true,
                AllowTailoredExperiences = false,
                AllowAdvertisingId = false,
                AllowOnlineSpeechRecognition = false,
                AllowInkingAndTypingDiagnostics = false,
                LocationAccess = DeployOobeLocationAccessMode.ForceOff
            },
            "amd64",
            workingDirectory,
            workspace.RootPath,
            TestContext.Current.CancellationToken);

        string unattendPath = Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml");
        XDocument document = XDocument.Load(unattendPath);
        XNamespace ns = "urn:schemas-microsoft-com:unattend";

        Assert.Equal("true", document.Descendants(ns + "HideEULAPage").Single().Value);
        Assert.Equal("3", document.Descendants(ns + "ProtectYourPC").Single().Value);
        Assert.Contains(processRunner.Calls, call => call.Contains(@"AllowTelemetry", StringComparison.Ordinal) && call.Contains("/d 0", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"DisablePrivacyExperience", StringComparison.Ordinal) && call.Contains("/d 1", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"DisabledByGroupPolicy", StringComparison.Ordinal) && call.Contains("/d 1", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"AllowInputPersonalization", StringComparison.Ordinal) && call.Contains("/d 0", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"AllowLinguisticDataCollection", StringComparison.Ordinal) && call.Contains("/d 0", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"LetAppsAccessLocation", StringComparison.Ordinal) && call.Contains("/d 2", StringComparison.Ordinal));
        Assert.DoesNotContain(processRunner.Calls, call => call.Contains(@"DisableLocation", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"DisableTailoredExperiencesWithDiagnosticData", StringComparison.Ordinal) && call.Contains("/d 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigureOfflineOobeAsync_WhenDisabled_DoesNotWriteUnattendOrPolicies()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = CreateWindowsRoot(workspace);
        string workingDirectory = Path.Combine(workspace.RootPath, "Work");
        var processRunner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        await service.ConfigureOfflineOobeAsync(
            windowsRoot,
            new DeployOobeSettings(),
            "amd64",
            workingDirectory,
            workspace.RootPath,
            TestContext.Current.CancellationToken);

        string unattendPath = Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml");

        Assert.False(File.Exists(unattendPath));
        Assert.Empty(processRunner.Calls);
    }

    [Fact]
    public async Task ConfigureOfflineOobeAsync_WhenAdministratorEnabled_WritesBlankPasswordAndActivationWithoutAutoLogon()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = CreateWindowsRoot(workspace);
        var service = new WindowsDeploymentService(new NoOpProcessRunner(), NullLogger<WindowsDeploymentService>.Instance);

        await service.ConfigureOfflineOobeAsync(
            windowsRoot,
            new DeployOobeSettings
            {
                IsEnabled = true,
                EnableAdministratorAccount = true,
                AdministratorPasswordIsBlank = true
            },
            "amd64",
            Path.Combine(workspace.RootPath, "Work"),
            workspace.RootPath,
            TestContext.Current.CancellationToken);

        XDocument document = XDocument.Load(Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml"));
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        XElement password = document.Descendants(ns + "AdministratorPassword").Single();

        Assert.Equal(string.Empty, password.Element(ns + "Value")?.Value);
        Assert.Equal("true", password.Element(ns + "PlainText")?.Value);
        XElement activationCommand = document.Descendants(ns + "RunSynchronousCommand").Single();
        XElement activationPath = activationCommand.Element(ns + "Path")!;
        Assert.InRange(activationPath.Value.Length, 1, 259);
        Assert.Equal(
            ["Description", "Order", "Path"],
            activationCommand.Elements().Select(element => element.Name.LocalName));
        Assert.Contains("*-500", activationCommand.Value, StringComparison.Ordinal);
        Assert.Equal("Microsoft-Windows-Deployment", activationCommand.Ancestors(ns + "component").Single().Attribute("name")?.Value);
        Assert.Empty(document.Descendants(ns + "AutoLogon"));
        Assert.Equal("false", document.Descendants(ns + "HideOnlineAccountScreens").Single().Value);
    }

    [Fact]
    public async Task ConfigureOfflineOobeAsync_WhenAdministratorPasswordIsEncrypted_HidesPlaintextInUnattend()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = CreateWindowsRoot(workspace);
        byte[] key = RandomNumberGenerator.GetBytes(DeployMediaSecretEnvelopeProtector.KeySizeBytes);
        const string plaintext = "Admin-Password-DoNotLeak";
        var keyProvider = new StaticDeploymentSecretKeyProvider(key);
        var service = new WindowsDeploymentService(
            new NoOpProcessRunner(),
            NullLogger<WindowsDeploymentService>.Instance,
            keyProvider);

        await service.ConfigureOfflineOobeAsync(
            windowsRoot,
            new DeployOobeSettings
            {
                IsEnabled = true,
                EnableAdministratorAccount = true,
                AdministratorPasswordSecret = EncryptSecret(plaintext, key)
            },
            "amd64",
            Path.Combine(workspace.RootPath, "Work"),
            workspace.RootPath,
            TestContext.Current.CancellationToken);

        string xml = File.ReadAllText(Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml"));
        XDocument document = XDocument.Parse(xml);
        XNamespace ns = "urn:schemas-microsoft-com:unattend";

        Assert.DoesNotContain(plaintext, xml, StringComparison.Ordinal);
        XElement password = document.Descendants(ns + "AdministratorPassword").Single();
        Assert.Equal("false", password.Element(ns + "PlainText")?.Value);
        Assert.Equal(
            plaintext + "AdministratorPassword",
            Encoding.Unicode.GetString(Convert.FromBase64String(password.Element(ns + "Value")!.Value)));
        Assert.Equal(workspace.RootPath, keyProvider.LastWorkspaceRootPath);
    }

    [Fact]
    public async Task ConfigureOfflineOobeAsync_WhenAdditionalAccountsConfigured_WritesAccountTypesAndPasswords()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = CreateWindowsRoot(workspace);
        byte[] key = RandomNumberGenerator.GetBytes(DeployMediaSecretEnvelopeProtector.KeySizeBytes);
        const string plaintext = "Standard-Password-DoNotLeak";
        var service = new WindowsDeploymentService(
            new NoOpProcessRunner(),
            NullLogger<WindowsDeploymentService>.Instance,
            new StaticDeploymentSecretKeyProvider(key));

        await service.ConfigureOfflineOobeAsync(
            windowsRoot,
            new DeployOobeSettings
            {
                IsEnabled = true,
                AdditionalAccounts =
                [
                    new DeployOobeAdditionalAccountSettings
                    {
                        Id = "standard",
                        UserName = "LocalUser",
                        Type = OobeAccountType.Standard,
                        PasswordSecret = EncryptSecret(plaintext, key)
                    },
                    new DeployOobeAdditionalAccountSettings
                    {
                        Id = "admin",
                        UserName = "LocalAdmin",
                        Type = OobeAccountType.Administrator,
                        PasswordIsBlank = true
                    }
                ]
            },
            "amd64",
            Path.Combine(workspace.RootPath, "Work"),
            workspace.RootPath,
            TestContext.Current.CancellationToken);

        string xml = File.ReadAllText(Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml"));
        XDocument document = XDocument.Parse(xml);
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        XElement[] accounts = document.Descendants(ns + "LocalAccount").ToArray();

        Assert.Equal(["LocalUser", "LocalAdmin"], accounts.Select(account => account.Element(ns + "Name")?.Value));
        Assert.Equal(["Users", "Administrators"], accounts.Select(account => account.Element(ns + "Group")?.Value));
        Assert.Equal("false", accounts[0].Descendants(ns + "PlainText").Single().Value);
        Assert.Equal(
            plaintext + "Password",
            Encoding.Unicode.GetString(Convert.FromBase64String(accounts[0].Descendants(ns + "Value").Single().Value)));
        Assert.Equal("true", accounts[1].Descendants(ns + "PlainText").Single().Value);
        Assert.DoesNotContain(plaintext, xml, StringComparison.Ordinal);
        Assert.Equal("true", document.Descendants(ns + "HideOnlineAccountScreens").Single().Value);
    }

    [Fact]
    public async Task ConfigureOfflineOobeAsync_PreservesExistingAccountsAndSpecializeCommands()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = CreateWindowsRoot(workspace);
        string unattendPath = Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(unattendPath)!);
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        new XDocument(
            new XElement(ns + "unattend",
                new XElement(ns + "settings",
                    new XAttribute("pass", "specialize"),
                    new XElement(ns + "component",
                        new XAttribute("name", "Microsoft-Windows-Deployment"),
                        new XElement(ns + "RunSynchronous",
                            new XElement(ns + "RunSynchronousCommand",
                                new XElement(ns + "Order", "4"),
                                new XElement(ns + "Description", "Keep me"),
                                new XElement(ns + "Path", "cmd.exe /c exit 0"))))),
                new XElement(ns + "settings",
                    new XAttribute("pass", "oobeSystem"),
                    new XElement(ns + "component",
                        new XAttribute("name", "Microsoft-Windows-Shell-Setup"),
                        new XElement(ns + "UserAccounts",
                            new XElement(ns + "LocalAccounts",
                                new XElement(ns + "LocalAccount",
                                    new XElement(ns + "Name", "ExistingUser"))))))))
            .Save(unattendPath);
        var service = new WindowsDeploymentService(new NoOpProcessRunner(), NullLogger<WindowsDeploymentService>.Instance);

        await service.ConfigureOfflineOobeAsync(
            windowsRoot,
            new DeployOobeSettings
            {
                IsEnabled = true,
                EnableAdministratorAccount = true,
                AdministratorPasswordIsBlank = true,
                AdditionalAccounts =
                [
                    new DeployOobeAdditionalAccountSettings
                    {
                        Id = "new-account",
                        UserName = "NewUser",
                        Type = OobeAccountType.Standard,
                        PasswordIsBlank = true
                    }
                ]
            },
            "amd64",
            Path.Combine(workspace.RootPath, "Work"),
            workspace.RootPath,
            TestContext.Current.CancellationToken);

        XDocument document = XDocument.Load(unattendPath);
        Assert.Equal(
            ["ExistingUser", "NewUser"],
            document.Descendants(ns + "LocalAccount").Select(account => account.Element(ns + "Name")?.Value));
        Assert.Equal(
            ["Keep me", "Enable built-in Administrator account"],
            document.Descendants(ns + "RunSynchronousCommand").Select(command => command.Element(ns + "Description")?.Value));
        Assert.Equal(
            ["4", "5"],
            document.Descendants(ns + "RunSynchronousCommand").Select(command => command.Element(ns + "Order")?.Value));
    }

    [Fact]
    public async Task ConfigureOfflineOobeAsync_WhenEncryptedPasswordHasNoKeyProvider_ThrowsWithoutLeakingPassword()
    {
        using var workspace = new TemporaryWorkspace();
        byte[] key = RandomNumberGenerator.GetBytes(DeployMediaSecretEnvelopeProtector.KeySizeBytes);
        const string plaintext = "Missing-Key-Password-DoNotLeak";
        var service = new WindowsDeploymentService(new NoOpProcessRunner(), NullLogger<WindowsDeploymentService>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConfigureOfflineOobeAsync(
                CreateWindowsRoot(workspace),
                new DeployOobeSettings
                {
                    IsEnabled = true,
                    EnableAdministratorAccount = true,
                    AdministratorPasswordSecret = EncryptSecret(plaintext, key)
                },
                "amd64",
                Path.Combine(workspace.RootPath, "Work"),
                workspace.RootPath,
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(plaintext, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureOfflineAiComponentRemovalAsync_WhenEnabled_WritesOfflinePolicies()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = CreateWindowsRoot(workspace);
        string workingDirectory = Path.Combine(workspace.RootPath, "Work");
        var processRunner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        await service.ConfigureOfflineAiComponentRemovalAsync(
            windowsRoot,
            new DeployAiComponentRemovalSettings
            {
                IsEnabled = true,
                RemoveCopilot = true,
                RemoveAiHub = true,
                DisableRecall = true,
                DisableClickToDo = true,
                DisableAiServiceAutoStart = true,
                DisableEdgeAi = true,
                DisablePaintAi = true,
                DisableNotepadAi = true
            },
            workingDirectory);

        Assert.Contains(processRunner.Calls, call => call.Contains(@"LOAD HKLM\FoundrySoftware", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"LOAD HKLM\FoundrySystem", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"LOAD HKU\FoundryDefault", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"WindowsCopilot", StringComparison.Ordinal) && call.Contains("TurnOffWindowsCopilot", StringComparison.Ordinal) && call.Contains("/d 1", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"WindowsAI", StringComparison.Ordinal) && call.Contains("DisableAIDataAnalysis", StringComparison.Ordinal) && call.Contains("/d 1", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"WindowsAI", StringComparison.Ordinal) && call.Contains("DisableClickToDo", StringComparison.Ordinal) && call.Contains("/d 1", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"ControlSet001\Services\WSAIFabricSvc", StringComparison.Ordinal) && call.Contains("Start", StringComparison.Ordinal) && call.Contains("/d 3", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"Policies\Microsoft\Edge", StringComparison.Ordinal) && call.Contains("CopilotPageContext", StringComparison.Ordinal) && call.Contains("/d 0", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"Policies\Paint", StringComparison.Ordinal) && call.Contains("DisableCocreator", StringComparison.Ordinal) && call.Contains("/d 1", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"Policies\WindowsNotepad", StringComparison.Ordinal) && call.Contains("DisableAIFeatures", StringComparison.Ordinal) && call.Contains("/d 1", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"FoundryDefault\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", StringComparison.Ordinal) && call.Contains("ShowCopilotButton", StringComparison.Ordinal) && call.Contains("/d 0", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"UNLOAD HKLM\FoundrySoftware", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"UNLOAD HKLM\FoundrySystem", StringComparison.Ordinal));
        Assert.Contains(processRunner.Calls, call => call.Contains(@"UNLOAD HKU\FoundryDefault", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigureOfflineAiComponentRemovalAsync_WhenDisabled_DoesNotWritePolicies()
    {
        using var workspace = new TemporaryWorkspace();
        string windowsRoot = CreateWindowsRoot(workspace);
        string workingDirectory = Path.Combine(workspace.RootPath, "Work");
        var processRunner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(processRunner, NullLogger<WindowsDeploymentService>.Instance);

        await service.ConfigureOfflineAiComponentRemovalAsync(
            windowsRoot,
            new DeployAiComponentRemovalSettings(),
            workingDirectory);

        Assert.Empty(processRunner.Calls);
    }

    private static string CreateWindowsRoot(TemporaryWorkspace workspace)
    {
        string windowsRoot = Path.Combine(workspace.RootPath, "WindowsRoot");
        Directory.CreateDirectory(Path.Combine(windowsRoot, "Windows", "System32", "config"));
        Directory.CreateDirectory(Path.Combine(windowsRoot, "Users", "Default"));
        File.WriteAllText(Path.Combine(windowsRoot, "Windows", "System32", "config", "SOFTWARE"), string.Empty);
        File.WriteAllText(Path.Combine(windowsRoot, "Windows", "System32", "config", "SYSTEM"), string.Empty);
        File.WriteAllText(Path.Combine(windowsRoot, "Users", "Default", "NTUSER.DAT"), string.Empty);
        return windowsRoot;
    }

    private static int ParseRequestedIndex(string arguments)
    {
        string value = arguments[(arguments.LastIndexOf("/Index:", StringComparison.OrdinalIgnoreCase) + 7)..];
        return int.Parse(value);
    }

    private static Foundry.Deploy.Models.Configuration.SecretEnvelope EncryptSecret(string plaintext, byte[] key)
    {
        byte[] payload = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[payload.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, payload, ciphertext, tag);
        CryptographicOperations.ZeroMemory(payload);

        return new Foundry.Deploy.Models.Configuration.SecretEnvelope
        {
            Kind = DeployMediaSecretEnvelopeProtector.Kind,
            Algorithm = DeployMediaSecretEnvelopeProtector.Algorithm,
            KeyId = DeployMediaSecretEnvelopeProtector.DeploymentKeyId,
            Nonce = Base64UrlEncode(nonce),
            Tag = Base64UrlEncode(tag),
            Ciphertext = Base64UrlEncode(ciphertext)
        };
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class StaticDeploymentSecretKeyProvider(byte[] key) : IDeploymentSecretKeyProvider
    {
        public string? LastWorkspaceRootPath { get; private set; }

        public Task<byte[]> ReadAsync(string workspaceRootPath, CancellationToken cancellationToken = default)
        {
            LastWorkspaceRootPath = workspaceRootPath;
            return Task.FromResult(key.ToArray());
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"foundry-deploy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class NoOpProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessExecutionResult { ExitCode = 0 });
        }

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessExecutionResult { ExitCode = 0 });
        }

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            Action<string>? onOutputData,
            Action<string>? onErrorData,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessExecutionResult { ExitCode = 0 });
        }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public string? LastFileName { get; private set; }
        public string? LastArguments { get; private set; }
        public IReadOnlyList<string>? LastArgumentTokens { get; private set; }
        public string? LastWorkingDirectory { get; private set; }
        public ProcessExecutionResult Result { get; init; } = new() { ExitCode = 0 };
        public Func<string, ProcessExecutionResult>? ResultFactory { get; init; }

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"{fileName} {arguments}");
            LastFileName = fileName;
            LastArguments = arguments;
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(ResultFactory?.Invoke(arguments) ?? Result);
        }

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            LastArgumentTokens = arguments.ToArray();
            string joinedArguments = string.Join(' ', LastArgumentTokens);
            Calls.Add($"{fileName} {joinedArguments}");
            LastFileName = fileName;
            LastArguments = joinedArguments;
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(ResultFactory?.Invoke(joinedArguments) ?? Result);
        }

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            Action<string>? onOutputData,
            Action<string>? onErrorData,
            CancellationToken cancellationToken = default)
        {
            LastArgumentTokens = arguments.ToArray();
            string joinedArguments = string.Join(' ', LastArgumentTokens);
            Calls.Add($"{fileName} {joinedArguments}");
            LastFileName = fileName;
            LastArguments = joinedArguments;
            LastWorkingDirectory = workingDirectory;
            ProcessExecutionResult result = ResultFactory?.Invoke(joinedArguments) ?? Result;
            if (!string.IsNullOrEmpty(result.StandardOutput))
            {
                onOutputData?.Invoke(result.StandardOutput);
            }

            if (!string.IsNullOrEmpty(result.StandardError))
            {
                onErrorData?.Invoke(result.StandardError);
            }

            return Task.FromResult(result);
        }
    }
}
