// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.ApplicationShell;
using Foundry.Deploy.Services.Deployment;
using System.Globalization;

namespace Foundry.Deploy.Tests;

public sealed class DeploymentLaunchPreparationServiceTests
{
    [Fact]
    public void Prepare_WhenDebugDiskUsedLive_BlocksBeforeConfirmation()
    {
        var shell = new FakeApplicationShellService { ConfirmationResult = true };
        DeploymentLaunchPreparationResult result = new DeploymentLaunchPreparationService(shell)
            .Prepare(CreateRequest(TargetDiskInfoFactory.CreateDebugVirtualDisk()));
        Assert.False(result.IsReadyToStart);
        Assert.Equal(0, shell.ConfirmationCallCount);
    }

    [Fact]
    public void Prepare_RetainsExactlyTheConfirmedSnapshot()
    {
        var shell = new FakeApplicationShellService { ConfirmationResult = true };
        var disk = CreateDisk() with { DiskNumber = 9, UniqueId = "A", SerialNumber = "serial" };
        DeploymentLaunchPreparationResult result = new DeploymentLaunchPreparationService(shell).Prepare(CreateRequest(disk));
        Assert.NotNull(result.Context);
        Assert.Equal(new TargetDiskIdentity(9, "A", "serial", disk.SizeBytes, disk.BusType), result.Context.ConfirmedTargetDisk);
        Assert.Same(disk, result.EffectiveTargetDisk);
    }
    [Fact]
    public void Prepare_WhenDryRunAndTargetDiskMissing_UsesDebugVirtualDisk()
    {
        var shell = new FakeApplicationShellService();
        var service = new DeploymentLaunchPreparationService(shell);

        DeploymentLaunchPreparationResult result = service.Prepare(CreateRequest(selectedTargetDisk: null, isDryRun: true));

        Assert.True(result.IsReadyToStart);
        Assert.Equal(999, result.EffectiveTargetDisk?.DiskNumber);
        Assert.Null(result.Context?.ConfirmedTargetDisk);
        Assert.Equal("LAB-01", result.NormalizedComputerName);
        Assert.Equal(0, shell.ConfirmationCallCount);
    }

    [Fact]
    public void Prepare_WhenSelectedDiskIsBlocked_FailsBeforeConfirmation()
    {
        var shell = new FakeApplicationShellService();
        var service = new DeploymentLaunchPreparationService(shell);
        TargetDiskInfo blockedDisk = CreateDisk(isSelectable: false, selectionWarning: "System disk");

        DeploymentLaunchPreparationResult result = service.Prepare(CreateRequest(selectedTargetDisk: blockedDisk));

        Assert.False(result.IsReadyToStart);
        Assert.Null(result.Context);
        Assert.Equal(0, shell.ConfirmationCallCount);
    }

    [Fact]
    public void Prepare_WhenOemDriverPackSelectionHasNoPackage_FailsValidation()
    {
        var shell = new FakeApplicationShellService();
        var service = new DeploymentLaunchPreparationService(shell);

        DeploymentLaunchPreparationResult result = service.Prepare(
            CreateRequest(
                selectedTargetDisk: CreateDisk(),
                driverPackSelectionKind: DriverPackSelectionKind.OemCatalog,
                selectedDriverPack: null));

        Assert.False(result.IsReadyToStart);
        Assert.Null(result.Context);
    }

    [Fact]
    public void Prepare_WhenRequestIsValidAndConfirmed_ReturnsDeploymentContext()
    {
        var shell = new FakeApplicationShellService { ConfirmationResult = true };
        var service = new DeploymentLaunchPreparationService(shell);
        TargetDiskInfo targetDisk = CreateDisk();
        DriverPackCatalogItem driverPack = new()
        {
            Id = "pack-1",
            Manufacturer = "Dell",
            Name = "Dell 24H2",
            FileName = "pack.cab",
            DownloadUrl = "https://example.test/pack.cab",
            OsName = "Windows 11",
            OsReleaseId = "24H2",
            OsArchitecture = "x64"
        };
        AutopilotProfileCatalogItem autopilotProfile = new()
        {
            FolderName = "profile",
            DisplayName = "Corporate Profile",
            ConfigurationFilePath = @"C:\Autopilot\profile.json"
        };
        DeployOobeSettings oobe = new()
        {
            IsEnabled = true,
            DiagnosticDataLevel = DeployOobeDiagnosticDataLevel.Off,
            LocationAccess = DeployOobeLocationAccessMode.ForceOff
        };
        DeployAppxRemovalSettings appxRemoval = new()
        {
            IsEnabled = true,
            PackageNames = ["Microsoft.BingNews", "Microsoft.BingWeather"]
        };
        DeployAiComponentRemovalSettings aiComponentRemoval = new()
        {
            IsEnabled = true,
            RemoveCopilot = true,
            DisableRecall = true
        };
        DeployWindowsOptionalFeatureSettings windowsOptionalFeatures = new()
        {
            IsEnabled = true,
            Actions = [new DeployWindowsOptionalFeatureAction { Id = "wf:netfx3", Enable = true }]
        };

        DeploymentLaunchPreparationResult result = service.Prepare(
            CreateRequest(
                selectedTargetDisk: targetDisk,
                targetComputerName: " LAB_01 ",
                driverPackSelectionKind: DriverPackSelectionKind.OemCatalog,
                selectedDriverPack: driverPack,
                defaultTimeZoneId: " Romance Standard Time ",
                isAutopilotEnabled: true,
                selectedAutopilotProfile: autopilotProfile,
                oobe: oobe,
                appxRemoval: appxRemoval,
                aiComponentRemoval: aiComponentRemoval,
                windowsOptionalFeatures: windowsOptionalFeatures));

        Assert.True(result.IsReadyToStart);
        Assert.Equal("LAB01", result.NormalizedComputerName);
        Assert.Equal(1, shell.ConfirmationCallCount);
        Assert.Equal(targetDisk.DiskNumber, result.Context?.TargetDiskNumber);
        Assert.Equal("LAB01", result.Context?.TargetComputerName);
        Assert.Equal("Romance Standard Time", result.Context?.DefaultTimeZoneId);
        Assert.Same(driverPack, result.Context?.DriverPack);
        Assert.Same(autopilotProfile, result.Context?.SelectedAutopilotProfile);
        Assert.Same(oobe, result.Context?.Oobe);
        Assert.Same(appxRemoval, result.Context?.AppxRemoval);
        Assert.Same(aiComponentRemoval, result.Context?.AiComponentRemoval);
        Assert.Same(windowsOptionalFeatures, result.Context?.WindowsOptionalFeatures);
    }

    [Fact]
    public void Prepare_WhenCompletionRebootIsDisabled_RetainsConfiguredDelay()
    {
        var shell = new FakeApplicationShellService();
        var service = new DeploymentLaunchPreparationService(shell);
        DeployCompletionSettings completion = new()
        {
            AutomaticRebootEnabled = false,
            AutomaticRebootDelaySeconds = 42
        };

        DeploymentLaunchPreparationResult result = service.Prepare(
            CreateRequest(
                selectedTargetDisk: CreateDisk(),
                completion: completion,
                isDryRun: true));

        Assert.True(result.IsReadyToStart);
        Assert.False(result.Context!.Completion.AutomaticRebootEnabled);
        Assert.Equal(42, result.Context.Completion.AutomaticRebootDelaySeconds);
    }

    [Fact]
    public void Prepare_WhenHardwareHashUploadModeHasNoJsonProfile_ReturnsDeploymentContext()
    {
        var shell = new FakeApplicationShellService { ConfirmationResult = true };
        var service = new DeploymentLaunchPreparationService(shell);

        DeploymentLaunchPreparationResult result = service.Prepare(
            CreateRequest(
                selectedTargetDisk: CreateDisk(),
                isAutopilotEnabled: true,
                autopilotProvisioningMode: AutopilotProvisioningMode.HardwareHashUpload,
                selectedAutopilotProfile: null,
                isDryRun: true));

        Assert.True(result.IsReadyToStart);
        Assert.Equal(AutopilotProvisioningMode.HardwareHashUpload, result.Context?.AutopilotProvisioningMode);
        Assert.Null(result.Context?.SelectedAutopilotProfile);
    }

    [Fact]
    public void Prepare_WhenInteractiveHardwareHashUploadModeHasNoJsonProfile_ReturnsDeploymentContext()
    {
        var shell = new FakeApplicationShellService { ConfirmationResult = true };
        var service = new DeploymentLaunchPreparationService(shell);

        DeploymentLaunchPreparationResult result = service.Prepare(
            CreateRequest(
                selectedTargetDisk: CreateDisk(),
                isAutopilotEnabled: true,
                autopilotProvisioningMode: AutopilotProvisioningMode.InteractiveHardwareHashUpload,
                selectedAutopilotProfile: null,
                isDryRun: true));

        Assert.True(result.IsReadyToStart);
        Assert.Equal(AutopilotProvisioningMode.InteractiveHardwareHashUpload, result.Context?.AutopilotProvisioningMode);
        Assert.Null(result.Context?.SelectedAutopilotProfile);
    }

    [Fact]
    public void Prepare_WhenLiveHardwareHashUploadModeIsSelected_DoesNotRequireJsonProfile()
    {
        var shell = new FakeApplicationShellService { ConfirmationResult = true };
        var service = new DeploymentLaunchPreparationService(shell);
        DeployAutopilotHardwareHashUploadSettings hardwareHashUpload = new()
        {
            TenantId = "tenant-id",
            ClientId = "client-id",
            ActiveCertificateThumbprint = "ABCDEF123456",
            ActiveCertificateExpiresOnUtc = DateTimeOffset.UtcNow.AddMonths(1),
            DefaultGroupTag = "Sales"
        };

        DeploymentLaunchPreparationResult result = service.Prepare(
            CreateRequest(
                selectedTargetDisk: CreateDisk(),
                isAutopilotEnabled: true,
                autopilotProvisioningMode: AutopilotProvisioningMode.HardwareHashUpload,
                selectedAutopilotProfile: null,
                autopilotHardwareHashUpload: hardwareHashUpload,
                isDryRun: false));

        Assert.True(result.IsReadyToStart);
        Assert.Equal(AutopilotProvisioningMode.HardwareHashUpload, result.Context?.AutopilotProvisioningMode);
        Assert.Null(result.Context?.SelectedAutopilotProfile);
        Assert.Same(hardwareHashUpload, result.Context?.AutopilotHardwareHashUpload);
        Assert.Equal(1, shell.ConfirmationCallCount);
    }

    [Fact]
    public void Prepare_WhenJsonProfileModeHasNoProfile_FailsValidation()
    {
        var shell = new FakeApplicationShellService();
        var service = new DeploymentLaunchPreparationService(shell);

        DeploymentLaunchPreparationResult result = service.Prepare(
            CreateRequest(
                selectedTargetDisk: CreateDisk(),
                isAutopilotEnabled: true,
                autopilotProvisioningMode: AutopilotProvisioningMode.JsonProfile,
                selectedAutopilotProfile: null));

        Assert.False(result.IsReadyToStart);
        Assert.Null(result.Context);
        Assert.Equal(0, shell.ConfirmationCallCount);
    }

    [Fact]
    public void Prepare_WhenConfirmationIsShown_UsesLocalizedWarningText()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            var shell = new FakeApplicationShellService { ConfirmationResult = true };
            var service = new DeploymentLaunchPreparationService(shell);

            TargetDiskInfo targetDisk = CreateDisk(sizeBytes: 0);

            service.Prepare(CreateRequest(selectedTargetDisk: targetDisk));

            Assert.Equal("Confirmer l’effacement du disque", shell.LastConfirmationTitle);
            Assert.Contains("Cela effacera toutes les données du disque sélectionné et installera le système d’exploitation sélectionné.", shell.LastConfirmationMessage);
            Assert.Contains("Disque : 3", shell.LastConfirmationMessage);
            Assert.Contains("Taille : Taille inconnue", shell.LastConfirmationMessage);
            Assert.Contains("Continuer le déploiement ?", shell.LastConfirmationMessage);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static DeploymentLaunchRequest CreateRequest(
        TargetDiskInfo? selectedTargetDisk,
        string targetComputerName = "LAB-01",
        string? defaultTimeZoneId = null,
        DriverPackSelectionKind driverPackSelectionKind = DriverPackSelectionKind.None,
        DriverPackCatalogItem? selectedDriverPack = null,
        bool isAutopilotEnabled = false,
        AutopilotProvisioningMode autopilotProvisioningMode = AutopilotProvisioningMode.JsonProfile,
        AutopilotProfileCatalogItem? selectedAutopilotProfile = null,
        DeployAutopilotHardwareHashUploadSettings? autopilotHardwareHashUpload = null,
        DeployOobeSettings? oobe = null,
        DeployAppxRemovalSettings? appxRemoval = null,
        DeployAiComponentRemovalSettings? aiComponentRemoval = null,
        DeployWindowsOptionalFeatureSettings? windowsOptionalFeatures = null,
        DeployCompletionSettings? completion = null,
        bool isDryRun = false)
    {
        return new DeploymentLaunchRequest
        {
            Mode = DeploymentMode.Usb,
            CacheRootPath = @"X:\Foundry\Runtime",
            TargetComputerName = targetComputerName,
            DefaultTimeZoneId = defaultTimeZoneId,
            SelectedTargetDisk = selectedTargetDisk,
            SelectedOperatingSystem = new OperatingSystemCatalogItem
            {
                WindowsRelease = "11",
                ReleaseId = "24H2",
                Architecture = "x64",
                LanguageCode = "en-US",
                Language = "English",
                Edition = "Professional",
                LicenseChannel = "Retail",
                Build = "26100"
            },
            DriverPackSelectionKind = driverPackSelectionKind,
            SelectedDriverPack = selectedDriverPack,
            ApplyFirmwareUpdates = false,
            IsAutopilotEnabled = isAutopilotEnabled,
            AutopilotProvisioningMode = autopilotProvisioningMode,
            SelectedAutopilotProfile = selectedAutopilotProfile,
            AutopilotHardwareHashUpload = autopilotHardwareHashUpload ?? new DeployAutopilotHardwareHashUploadSettings(),
            Oobe = oobe ?? new DeployOobeSettings(),
            AppxRemoval = appxRemoval ?? new DeployAppxRemovalSettings(),
            AiComponentRemoval = aiComponentRemoval ?? new DeployAiComponentRemovalSettings(),
            WindowsOptionalFeatures = windowsOptionalFeatures ?? new DeployWindowsOptionalFeatureSettings(),
            Completion = completion ?? new DeployCompletionSettings(),
            IsDryRun = isDryRun
        };
    }

    private static TargetDiskInfo CreateDisk(bool isSelectable = true, string selectionWarning = "", ulong sizeBytes = 256UL * 1024UL * 1024UL * 1024UL)
    {
        return new TargetDiskInfo
        {
            DiskNumber = 3,
            FriendlyName = "NVMe Disk",
            BusType = "NVMe",
            SizeBytes = sizeBytes,
            IsSelectable = isSelectable,
            SelectionWarning = selectionWarning
        };
    }

    private sealed class FakeApplicationShellService : IApplicationShellService
    {
        public bool ConfirmationResult { get; init; } = true;

        public int ConfirmationCallCount { get; private set; }

        public string LastConfirmationTitle { get; private set; } = string.Empty;

        public string LastConfirmationMessage { get; private set; } = string.Empty;

        public void ShowAbout()
        {
        }

        public bool ConfirmWarning(string title, string message)
        {
            ConfirmationCallCount++;
            LastConfirmationTitle = title;
            LastConfirmationMessage = message;
            return ConfirmationResult;
        }

        public void ShowBlockingError(string title, string message)
        {
        }

        public void Shutdown()
        {
        }
    }
}
