// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace Foundry.Deploy.Services.Localization;

/// <summary>
/// Translates deployment status values emitted by runtime services before they are shown by the WPF shell.
/// </summary>
public static partial class DeploymentUiTextLocalizer
{
    /// <summary>
    /// Localizes an invariant deployment step name while preserving unknown labels from external sources.
    /// </summary>
    /// <param name="value">The invariant step name produced by the deployment pipeline.</param>
    /// <returns>The localized step name, or a localized status message when the value is not a known step.</returns>
    public static string LocalizeStepName(string value)
    {
        return value switch
        {
            "Gather deployment variables" => LocalizationText.GetString("Step.GatherDeploymentVariables"),
            "Initialize deployment workspace" => LocalizationText.GetString("Step.InitializeDeploymentWorkspace"),
            "Validate target configuration" => LocalizationText.GetString("Step.ValidateTargetConfiguration"),
            "Resolve cache strategy" => LocalizationText.GetString("Step.ResolveCacheStrategy"),
            "Prepare target disk layout" => LocalizationText.GetString("Step.PrepareTargetDiskLayout"),
            "Download operating system image" => LocalizationText.GetString("Step.DownloadOperatingSystemImage"),
            "Validate custom answer file" => LocalizationText.GetString("Step.ValidateCustomUnattend"),
            "Stage custom answer file" => LocalizationText.GetString("Step.StageCustomUnattend"),
            "Apply operating system image" => LocalizationText.GetString("Step.ApplyOperatingSystemImage"),
            "Configure target computer name" => LocalizationText.GetString("Step.ConfigureTargetComputerName"),
            "Configure OOBE settings" => LocalizationText.GetString("Step.ConfigureOobeSettings"),
            "Configure Windows optional features" => LocalizationText.GetString("Step.ConfigureWindowsOptionalFeatures"),
            "Configure recovery environment" => LocalizationText.GetString("Step.ConfigureRecoveryEnvironment"),
            "Download driver pack" => LocalizationText.GetString("Step.DownloadDriverPack"),
            "Extract driver pack" => LocalizationText.GetString("Step.ExtractDriverPack"),
            "Stage pre-OOBE customization" => LocalizationText.GetString("Step.StagePreOobeCustomization"),
            "Apply driver pack" => LocalizationText.GetString("Step.ApplyDriverPack"),
            "Apply recovery drivers" => LocalizationText.GetString("Step.ApplyRecoveryDrivers"),
            "Download firmware update" => LocalizationText.GetString("Step.DownloadFirmwareUpdate"),
            "Apply firmware update" => LocalizationText.GetString("Step.ApplyFirmwareUpdate"),
            "Seal recovery partition" => LocalizationText.GetString("Step.SealRecoveryPartition"),
            "Provision Autopilot" => LocalizationText.GetString("Step.ProvisionAutopilot"),
            "Finalize deployment and write logs" => LocalizationText.GetString("Step.FinalizeDeploymentAndWriteLogs"),
            _ => LocalizeMessage(value)
        };
    }

    /// <summary>
    /// Localizes invariant deployment messages and generated progress labels for the active UI culture.
    /// </summary>
    /// <param name="value">The invariant message emitted by services, progress reporters, or view models.</param>
    /// <returns>The localized message, or the original value when no safe mapping exists.</returns>
    public static string LocalizeMessage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value switch
        {
            "Waiting for deployment..." => LocalizationText.GetString("Status.WaitingForDeployment"),
            "Waiting for progress..." => LocalizationText.GetString("Status.WaitingForProgress"),
            "Preparing deployment..." => LocalizationText.GetString("Status.PreparingDeployment"),
            "Deployment cancelled." => LocalizationText.GetString("Status.DeploymentCancelled"),
            "Starting step..." => LocalizationText.GetString("Status.StartingStep"),
            "Step completed." => LocalizationText.GetString("Status.StepCompleted"),
            "Step failed." => LocalizationText.GetString("Status.StepFailed"),
            "Step skipped." => LocalizationText.GetString("Status.StepSkipped"),
            "In progress..." => LocalizationText.GetString("Status.InProgress"),
            "Detecting hardware..." => LocalizationText.GetString("Preparation.DetectingHardware"),
            "Hardware detection failed." => LocalizationText.GetString("Preparation.HardwareDetectionFailed"),
            "Another operation is already running." => LocalizationText.GetString("Status.AnotherOperationRunning"),
            "Gathering deployment variables..." => LocalizationText.GetString("StepMessage.GatheringDeploymentVariables"),
            "Collecting deployment context..." => LocalizationText.GetString("StepMessage.CollectingDeploymentContext"),
            "Deployment variables gathered." => LocalizationText.GetString("StepResult.DeploymentVariablesGathered"),
            "Deployment variables gathered (simulation)." => LocalizationText.GetString("StepResult.DeploymentVariablesGatheredSimulation"),
            "Initializing deployment workspace..." => LocalizationText.GetString("StepMessage.InitializingDeploymentWorkspace"),
            "Creating workspace folders..." => LocalizationText.GetString("StepMessage.CreatingWorkspaceFolders"),
            "Finalizing deployment..." => LocalizationText.GetString("StepMessage.FinalizingDeployment"),
            "Cleaning temporary workspace..." => LocalizationText.GetString("StepMessage.CleaningTemporaryWorkspace"),
            "Writing deployment summary..." => LocalizationText.GetString("StepMessage.WritingDeploymentSummary"),
            "Writing completion logs..." => LocalizationText.GetString("StepMessage.WritingCompletionLogs"),
            "Workspace initialized." => LocalizationText.GetString("StepResult.WorkspaceInitialized"),
            "Workspace initialized (simulation)." => LocalizationText.GetString("StepResult.WorkspaceInitializedSimulation"),
            "Validating target configuration..." => LocalizationText.GetString("StepMessage.ValidatingTargetConfiguration"),
            "Revalidating target disk..." => LocalizationText.GetString("StepMessage.RevalidatingTargetDisk"),
            "Detecting hardware profile..." => LocalizationText.GetString("StepMessage.DetectingHardwareProfile"),
            "Operating system URL is missing." => LocalizationText.GetString("StepResult.OperatingSystemUrlMissing"),
            "Target disk number is required." => LocalizationText.GetString("StepResult.TargetDiskNumberRequired"),
            "Target configuration validated." => LocalizationText.GetString("StepResult.TargetConfigurationValidated"),
            "Target configuration validated (simulation)." => LocalizationText.GetString("StepResult.TargetConfigurationValidatedSimulation"),
            "Resolving cache strategy..." => LocalizationText.GetString("StepMessage.ResolvingCacheStrategy"),
            "Resolving cache location..." => LocalizationText.GetString("StepMessage.ResolvingCacheLocation"),
            "Checking cache disk conflict..." => LocalizationText.GetString("StepMessage.CheckingCacheDiskConflict"),
            "Cache strategy resolved." => LocalizationText.GetString("StepResult.CacheStrategyResolved"),
            "Cache strategy resolved (simulation)." => LocalizationText.GetString("StepResult.CacheStrategyResolvedSimulation"),
            "Preparing target disk layout..." => LocalizationText.GetString("StepMessage.PreparingTargetDiskLayout"),
            "Partitioning target disk..." => LocalizationText.GetString("StepMessage.PartitioningTargetDisk"),
            "Preparing target workspace..." => LocalizationText.GetString("StepMessage.PreparingTargetWorkspace"),
            "Creating simulated partitions..." => LocalizationText.GetString("StepMessage.CreatingSimulatedPartitions"),
            "Target disk layout prepared." => LocalizationText.GetString("StepResult.TargetDiskLayoutPrepared"),
            "Target disk layout prepared (simulation)." => LocalizationText.GetString("StepResult.TargetDiskLayoutPreparedSimulation"),
            "Target disk layout was not prepared." => LocalizationText.GetString("StepResult.TargetDiskLayoutNotPrepared"),
            "Downloading OS image..." => LocalizationText.GetString("StepMessage.DownloadingOperatingSystemImage"),
            "Checking cache..." => LocalizationText.GetString("StepMessage.CheckingCache"),
            "Operating system image ready (simulation)." => LocalizationText.GetString("StepResult.OperatingSystemImageReadySimulation"),
            "Operating system image was not downloaded." => LocalizationText.GetString("StepResult.OperatingSystemImageNotDownloaded"),
            "Operating system image downloaded." => LocalizationText.GetString("StepResult.OperatingSystemImageDownloaded"),
            "Operating system image resolved from cache." => LocalizationText.GetString("StepResult.OperatingSystemImageResolvedFromCache"),
            "Applying OS image..." => LocalizationText.GetString("StepMessage.ApplyingOperatingSystemImage"),
            "Inspecting image..." => LocalizationText.GetString("StepMessage.InspectingImage"),
            "Applying image..." => LocalizationText.GetString("StepMessage.ApplyingImage"),
            "Configuring boot..." => LocalizationText.GetString("StepMessage.ConfiguringBoot"),
            "Verifying image..." => LocalizationText.GetString("StepMessage.VerifyingImage"),
            "Operating system image applied." => LocalizationText.GetString("StepResult.OperatingSystemImageApplied"),
            "Operating system image applied (simulation)." => LocalizationText.GetString("StepResult.OperatingSystemImageAppliedSimulation"),
            "Configuring target computer name..." => LocalizationText.GetString("StepMessage.ConfiguringTargetComputerName"),
            "Writing offline computer name..." => LocalizationText.GetString("StepMessage.WritingOfflineComputerName"),
            "Target Windows partition is unavailable." => LocalizationText.GetString("StepResult.TargetWindowsPartitionUnavailable"),
            "Applied Windows image index is unavailable." => LocalizationText.GetString("StepResult.AppliedWindowsImageIndexUnavailable"),
            "Target computer name configured." => LocalizationText.GetString("StepResult.TargetComputerNameConfigured"),
            "Target computer name configured (simulation)." => LocalizationText.GetString("StepResult.TargetComputerNameConfiguredSimulation"),
            "Offline customization disabled." => LocalizationText.GetString("StepResult.OfflineCustomizationDisabled"),
            "Configuring offline customizations..." => LocalizationText.GetString("StepMessage.ConfiguringOfflineCustomizations"),
            "Writing first-run defaults..." => LocalizationText.GetString("StepMessage.WritingFirstRunDefaults"),
            "Configuring AI component removal..." => LocalizationText.GetString("StepMessage.ConfiguringAiComponentRemoval"),
            "Writing offline AI policies..." => LocalizationText.GetString("StepMessage.WritingOfflineAiPolicies"),
            "Offline customization configured." => LocalizationText.GetString("StepResult.OfflineCustomizationConfigured"),
            "Offline customization configured (simulation)." => LocalizationText.GetString("StepResult.OfflineCustomizationConfiguredSimulation"),
            "Configuring OOBE settings..." => LocalizationText.GetString("StepMessage.ConfiguringOobeSettings"),
            "Writing first-run privacy defaults..." => LocalizationText.GetString("StepMessage.WritingFirstRunPrivacyDefaults"),
            "Windows optional feature configuration disabled." => LocalizationText.GetString("StepResult.WindowsOptionalFeatureConfigurationDisabled"),
            "Windows optional feature configuration is invalid." => LocalizationText.GetString("StepResult.WindowsOptionalFeatureConfigurationInvalid"),
            "Configuring Windows optional features..." => LocalizationText.GetString("StepMessage.ConfiguringWindowsOptionalFeatures"),
            "Inspecting feature states..." => LocalizationText.GetString("StepMessage.InspectingWindowsOptionalFeatureStates"),
            "Preparing matching setup media sources..." => LocalizationText.GetString("StepMessage.PreparingWindowsOptionalFeatureSources"),
            "Applying feature changes..." => LocalizationText.GetString("StepMessage.ApplyingWindowsOptionalFeatureChanges"),
            "Simulating feature changes..." => LocalizationText.GetString("StepMessage.SimulatingWindowsOptionalFeatureChanges"),
            "Applying feature changes" => LocalizationText.GetString("StepProgress.ApplyingWindowsOptionalFeatureChanges"),
            "Configuring recovery environment..." => LocalizationText.GetString("StepMessage.ConfiguringRecoveryEnvironment"),
            "Preparing Windows Recovery Environment..." => LocalizationText.GetString("StepMessage.PreparingWindowsRecoveryEnvironment"),
            "Recovery partition is unavailable." => LocalizationText.GetString("StepResult.RecoveryPartitionUnavailable"),
            "Recovery environment configured." => LocalizationText.GetString("StepResult.RecoveryEnvironmentConfigured"),
            "Recovery environment configured (simulation)." => LocalizationText.GetString("StepResult.RecoveryEnvironmentConfiguredSimulation"),
            "No pre-OOBE customization scripts are required." => LocalizationText.GetString("StepResult.NoPreOobeCustomizationScriptsRequired"),
            "Staging pre-OOBE customizations..." => LocalizationText.GetString("StepMessage.StagingPreOobeCustomizations"),
            "Pre-OOBE customizations staged." => LocalizationText.GetString("StepResult.PreOobeCustomizationsStaged"),
            "Pre-OOBE customizations staged (simulation)." => LocalizationText.GetString("StepResult.PreOobeCustomizationsStagedSimulation"),
            "Driver pack disabled (None selected)." => LocalizationText.GetString("StepResult.DriverPackDisabled"),
            "No driver pack download required." => LocalizationText.GetString("StepResult.NoDriverPackDownloadRequired"),
            "Downloading driver pack..." => LocalizationText.GetString("StepMessage.DownloadingDriverPack"),
            "Preparing download..." => LocalizationText.GetString("StepMessage.PreparingDownload"),
            "OEM driver pack mode selected but no driver pack was provided." => LocalizationText.GetString("StepResult.OemDriverPackMissing"),
            "Driver pack downloaded." => LocalizationText.GetString("StepResult.DriverPackDownloaded"),
            "Driver pack downloaded (simulation)." => LocalizationText.GetString("StepResult.DriverPackDownloadedSimulation"),
            "Driver pack resolved from cache." => LocalizationText.GetString("StepResult.DriverPackResolvedFromCache"),
            "Extracting driver pack..." => LocalizationText.GetString("StepMessage.ExtractingDriverPack"),
            "Driver pack was not downloaded." => LocalizationText.GetString("StepResult.DriverPackNotDownloaded"),
            "Driver pack extracted." => LocalizationText.GetString("StepResult.DriverPackExtracted"),
            "Driver pack extracted (simulation)." => LocalizationText.GetString("StepResult.DriverPackExtractedSimulation"),
            "Driver pack prepared for deferred installation." => LocalizationText.GetString("StepResult.DriverPackPreparedForDeferredInstallation"),
            "No driver pack operation is required." => LocalizationText.GetString("StepResult.NoDriverPackOperationRequired"),
            "Unsupported driver pack install mode." => LocalizationText.GetString("StepResult.UnsupportedDriverPackInstallMode"),
            "No extracted INF driver payload is available." => LocalizationText.GetString("StepResult.NoExtractedInfDriverPayload"),
            "Driver pack source payload is unavailable for deferred staging." => LocalizationText.GetString("StepResult.DriverPackSourcePayloadUnavailableForDeferredStaging"),
            "Microsoft Update Catalog did not produce a driver payload." => LocalizationText.GetString("StepResult.MicrosoftUpdateCatalogDriverPayloadMissing"),
            "Deferred driver pack staging was requested without a supported deferred command." => LocalizationText.GetString("StepResult.DeferredDriverPackStagingUnsupportedCommand"),
            "Applying driver pack..." => LocalizationText.GetString("StepMessage.ApplyingDriverPack"),
            "Applying Windows drivers..." => LocalizationText.GetString("StepMessage.ApplyingWindowsDrivers"),
            "Mounting WinRE..." => LocalizationText.GetString("StepMessage.MountingWinRe"),
            "Applying WinRE drivers..." => LocalizationText.GetString("StepMessage.ApplyingWinReDrivers"),
            "Unmounting WinRE..." => LocalizationText.GetString("StepMessage.UnmountingWinRe"),
            "Staging package..." => LocalizationText.GetString("StepMessage.StagingPackage"),
            "Updating SetupComplete hook..." => LocalizationText.GetString("StepMessage.UpdatingSetupCompleteHook"),
            "Driver pack applied." => LocalizationText.GetString("StepResult.DriverPackApplied"),
            "Driver pack applied (simulation)." => LocalizationText.GetString("StepResult.DriverPackAppliedSimulation"),
            "Downloading firmware update..." => LocalizationText.GetString("StepMessage.DownloadingFirmwareUpdate"),
            "Preparing Microsoft Update Catalog lookup..." => LocalizationText.GetString("StepMessage.PreparingMicrosoftUpdateCatalogLookup"),
            "Firmware updates are disabled." => LocalizationText.GetString("StepResult.FirmwareUpdatesDisabled"),
            "Firmware updates are disabled for virtual machines." => LocalizationText.GetString("StepResult.FirmwareUpdatesDisabledForVirtualMachines"),
            "Firmware updates are skipped while the device is running on battery power." => LocalizationText.GetString("StepResult.FirmwareUpdatesSkippedOnBattery"),
            "System firmware hardware identifier is unavailable." => LocalizationText.GetString("StepResult.SystemFirmwareHardwareIdentifierUnavailable"),
            "Firmware update downloaded." => LocalizationText.GetString("StepResult.FirmwareUpdateDownloaded"),
            "Firmware update downloaded (simulation)." => LocalizationText.GetString("StepResult.FirmwareUpdateDownloadedSimulation"),
            "No extracted firmware payload is available." => LocalizationText.GetString("StepResult.NoExtractedFirmwarePayload"),
            "The extracted firmware payload does not contain any INF files." => LocalizationText.GetString("StepResult.NoFirmwareInfFiles"),
            "Applying firmware update..." => LocalizationText.GetString("StepMessage.ApplyingFirmwareUpdate"),
            "Injecting firmware payload into offline Windows..." => LocalizationText.GetString("StepMessage.InjectingFirmwarePayload"),
            "Firmware update applied." => LocalizationText.GetString("StepResult.FirmwareUpdateApplied"),
            "Firmware update applied (simulation)." => LocalizationText.GetString("StepResult.FirmwareUpdateAppliedSimulation"),
            "Sealing recovery partition..." => LocalizationText.GetString("StepMessage.SealingRecoveryPartition"),
            "Removing recovery drive letter..." => LocalizationText.GetString("StepMessage.RemovingRecoveryDriveLetter"),
            "Recovery partition sealed." => LocalizationText.GetString("StepResult.RecoveryPartitionSealed"),
            "Recovery partition sealed (simulation)." => LocalizationText.GetString("StepResult.RecoveryPartitionSealedSimulation"),
            "Autopilot is disabled." => LocalizationText.GetString("StepResult.AutopilotDisabled"),
            "Autopilot is enabled but no profile was selected." => LocalizationText.GetString("StepResult.AutopilotProfileMissing"),
            "Target Windows partition is unavailable for Autopilot staging." => LocalizationText.GetString("StepResult.TargetWindowsPartitionUnavailableForAutopilot"),
            "Failed to resolve the target Autopilot directory." => LocalizationText.GetString("StepResult.TargetAutopilotDirectoryUnavailable"),
            "Staging Autopilot profile..." => LocalizationText.GetString("StepMessage.StagingAutopilotProfile"),
            "Copying AutopilotConfigurationFile.json..." => LocalizationText.GetString("StepMessage.CopyingAutopilotConfiguration"),
            "Writing dry-run Autopilot manifest..." => LocalizationText.GetString("StepMessage.WritingDryRunAutopilotManifest"),
            "Capturing Autopilot hardware hash..." => LocalizationText.GetString("StepMessage.CapturingAutopilotHardwareHash"),
            "Running OA3Tool..." => LocalizationText.GetString("StepMessage.RunningOa3Tool"),
            "Uploading Autopilot hardware hash..." => LocalizationText.GetString("StepMessage.UploadingAutopilotHardwareHash"),
            "Staging Autopilot registration assistant..." => LocalizationText.GetString("StepMessage.StagingAutopilotRegistrationAssistant"),
            "Copying interactive registration files..." => LocalizationText.GetString("StepMessage.CopyingInteractiveRegistrationFiles"),
            "Writing dry-run interactive registration manifest..." => LocalizationText.GetString("StepMessage.WritingDryRunInteractiveRegistrationManifest"),
            "Preparing Microsoft Graph import..." => LocalizationText.GetString("StepMessage.PreparingMicrosoftGraphImport"),
            "Submitting import request to Microsoft Graph..." => LocalizationText.GetString("StepMessage.SubmittingMicrosoftGraphImport"),
            "Waiting for Autopilot device visibility..." => LocalizationText.GetString("StepMessage.WaitingForAutopilotDeviceVisibility"),
            "Updating existing Autopilot device..." => LocalizationText.GetString("StepMessage.UpdatingExistingAutopilotDevice"),
            "Updating Windows Autopilot group tag in Microsoft Graph..." => LocalizationText.GetString("StepMessage.UpdatingWindowsAutopilotGroupTag"),
            "Waiting for Autopilot group tag update..." => LocalizationText.GetString("StepMessage.WaitingForAutopilotGroupTagUpdate"),
            "Preparing Autopilot hardware hash upload..." => LocalizationText.GetString("StepMessage.PreparingAutopilotHardwareHashUpload"),
            "Decrypting media certificate..." => LocalizationText.GetString("StepMessage.DecryptingMediaCertificate"),
            "Authenticating Autopilot hardware hash upload..." => LocalizationText.GetString("StepMessage.AuthenticatingAutopilotHardwareHashUpload"),
            "Requesting Microsoft Graph token..." => LocalizationText.GetString("StepMessage.RequestingMicrosoftGraphToken"),
            "Importing hardware hash into Microsoft Graph..." => LocalizationText.GetString("StepMessage.ImportingHardwareHashIntoMicrosoftGraph"),
            "Writing dry-run Autopilot hash manifest..." => LocalizationText.GetString("StepMessage.WritingDryRunAutopilotHashManifest"),
            "Target Windows partition is unavailable for Autopilot hardware hash upload." => LocalizationText.GetString("StepResult.TargetWindowsPartitionUnavailableForAutopilotHashUpload"),
            "Target Windows partition is unavailable for interactive Autopilot registration assistant staging." => LocalizationText.GetString("StepResult.TargetWindowsPartitionUnavailableForInteractiveAutopilotRegistration"),
            "Autopilot hardware hash upload skipped because the embedded certificate is expired." => LocalizationText.GetString("StepResult.AutopilotHashUploadSkippedExpiredCertificate"),
            "Autopilot hardware hash upload skipped because media metadata is incomplete." => LocalizationText.GetString("StepResult.AutopilotHashUploadSkippedMissingMetadata"),
            "Autopilot hardware hash imported and visible in Windows Autopilot devices." => LocalizationText.GetString("StepResult.AutopilotHardwareHashImportedVisible"),
            "Imported Autopilot device did not appear in Windows Autopilot devices before the timeout." => LocalizationText.GetString("StepResult.AutopilotDeviceVisibilityTimedOut"),
            "Windows Autopilot device group tag update was not confirmed before the timeout." => LocalizationText.GetString("StepResult.AutopilotGroupTagUpdateTimedOut"),
            "Autopilot hardware hash upload prepared for dry run." => LocalizationText.GetString("StepResult.AutopilotHashUploadPreparedDryRun"),
            "Autopilot hardware hash upload prepared (simulation)." => LocalizationText.GetString("StepResult.AutopilotHashUploadPreparedSimulation"),
            "Interactive Autopilot registration assistant staged." => LocalizationText.GetString("StepResult.InteractiveAutopilotRegistrationAssistantStaged"),
            "Interactive Autopilot registration assistant staged (simulation)." => LocalizationText.GetString("StepResult.InteractiveAutopilotRegistrationAssistantStagedSimulation"),
            "Autopilot profile staged." => LocalizationText.GetString("StepResult.AutopilotProfileStaged"),
            "Autopilot profile staged (simulation)." => LocalizationText.GetString("StepResult.AutopilotProfileStagedSimulation"),
            "System reboot" => LocalizationText.GetString("Status.SystemReboot"),
            "Required reboot executable 'wpeutil.exe' was not found." => LocalizationText.GetString("Status.RequiredRebootExecutableMissing"),
            "Unknown step" => LocalizationText.GetString("Status.UnknownStep"),
            "No error details were provided." => LocalizationText.GetString("Status.NoErrorDetails"),
            "N/A" => LocalizationText.GetString("Common.NotAvailable"),
            "Unavailable" => LocalizationText.GetString("Common.Unavailable"),
            "None" => LocalizationText.GetString("Common.None"),
            "Blocked: system disk" => LocalizationText.GetString("Disk.BlockedSystemDisk"),
            "Blocked: boot disk" => LocalizationText.GetString("Disk.BlockedBootDisk"),
            "Blocked: read-only" => LocalizationText.GetString("Disk.BlockedReadOnly"),
            "Blocked: offline" => LocalizationText.GetString("Disk.BlockedOffline"),
            "Microsoft Update Catalog" => LocalizationText.GetString("DriverPack.MicrosoftUpdateCatalog"),
            "Downloading" => LocalizationText.GetString("StepProgress.Downloading"),
            "Extracting" => LocalizationText.GetString("StepProgress.Extracting"),
            "Applying" => LocalizationText.GetString("StepProgress.Applying"),
            "Staging" => LocalizationText.GetString("StepProgress.Staging"),
            _ => LocalizeDynamicMessage(value)
        };
    }

    private static string LocalizeDynamicMessage(string value)
    {
        Match match = CatalogLoadedRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "Catalog.LoadedFormat",
                int.Parse(match.Groups["os"].Value),
                int.Parse(match.Groups["drivers"].Value));
        }

        match = WindowsOptionalFeatureConfiguredRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "StepResult.WindowsOptionalFeaturesConfiguredFormat",
                int.Parse(match.Groups["changed"].Value),
                int.Parse(match.Groups["satisfied"].Value),
                int.Parse(match.Groups["unavailable"].Value));
        }

        match = WindowsOptionalFeatureSimulationRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "StepResult.WindowsOptionalFeaturesSimulationFormat",
                int.Parse(match.Groups["actions"].Value));
        }

        match = WindowsOptionalFeatureRemovedPayloadRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "StepResult.WindowsOptionalFeatureRemovedPayloadFormat",
                match.Groups["feature"].Value);
        }

        match = WindowsOptionalFeatureVerificationFailedRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "StepResult.WindowsOptionalFeatureVerificationFailedFormat",
                match.Groups["feature"].Value);
        }

        if (TryLocalizeSingleSuffix(
            value,
            "Matching NetFx3 source is unavailable. ",
            "StepResult.WindowsOptionalFeatureMatchingSourceUnavailableFormat",
            out string optionalFeatureSourceUnavailable))
        {
            return optionalFeatureSourceUnavailable;
        }

        if (TryLocalizeSingleSuffix(value, "Catalog load failed: ", "Catalog.LoadFailedFormat", out string localized))
        {
            return localized;
        }

        if (TryLocalizeSingleSuffix(value, "Target disk discovery failed: ", "Preparation.TargetDiskDiscoveryFailedFormat", out localized))
        {
            return localized;
        }

        if (TryLocalizeSingleSuffix(value, "Hardware detection failed: ", "Preparation.HardwareDetectionFailedFormat", out localized))
        {
            return localized;
        }

        match = TargetDiskMissingRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format("Disk.TargetMissingFormat", int.Parse(match.Groups["disk"].Value));
        }

        match = TargetDiskBlockedRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "Disk.TargetBlockedFormat",
                int.Parse(match.Groups["disk"].Value),
                LocalizeMessage(match.Groups["warning"].Value));
        }

        if (TryLocalizeSingleSuffix(value, "Selected Autopilot profile file was not found: ", "StepResult.SelectedAutopilotProfileFileMissingFormat", out localized))
        {
            return localized;
        }

        if (TryLocalizeSingleSuffix(value, "Autopilot hardware hash capture failed: ", "StepResult.AutopilotHashCaptureFailedFormat", out localized))
        {
            return localized;
        }

        if (TryLocalizeSingleSuffix(value, "Autopilot hardware hash import failed: ", "StepResult.AutopilotHashImportFailedFormat", out localized))
        {
            return localized;
        }

        if (TryLocalizeSingleSuffix(value, "Autopilot hardware hash upload skipped: ", "StepResult.AutopilotHashUploadSkippedFormat", out localized))
        {
            return localized;
        }

        match = CheckingWindowsAutopilotDevicesRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "StepMessage.CheckingWindowsAutopilotDevicesFormat",
                LocalizeCompactRemainingDuration(match.Groups["remaining"].Value));
        }

        match = CheckingWindowsAutopilotGroupTagRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "StepMessage.CheckingWindowsAutopilotGroupTagFormat",
                LocalizeCompactRemainingDuration(match.Groups["remaining"].Value));
        }

        if (value.StartsWith("Debug preview: DISM apply failed because the target partition is read-only.", StringComparison.Ordinal))
        {
            return LocalizationText.GetString("Debug.ErrorPreviewMessage");
        }

        match = StepPercentLabelRegex().Match(value);
        if (match.Success)
        {
            string rawLabel = match.Groups["label"].Value;
            string localizedLabel = LocalizeProgressLabel(rawLabel);
            if (!localizedLabel.Equals(rawLabel, StringComparison.Ordinal))
            {
                return LocalizationText.Format("StepProgress.PercentFormat", localizedLabel, match.Groups["percent"].Value);
            }
        }

        match = DownloadedBytesRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format("StepProgress.DownloadedBytesFormat", match.Groups["size"].Value);
        }

        match = StepCounterRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format(
                "Status.StepCounterFormat",
                int.Parse(match.Groups["current"].Value),
                int.Parse(match.Groups["total"].Value));
        }

        match = StartingStepRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format("Status.StartingStepFormat", LocalizeStepName(match.Groups["step"].Value));
        }

        match = StartingStepSubProgressRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format("Status.StartingStepSubProgressFormat", LocalizeStepName(match.Groups["step"].Value));
        }

        match = WpeUtilFailureRegex().Match(value);
        if (match.Success)
        {
            return LocalizationText.Format("Status.WpeUtilFailureFormat", match.Groups["code"].Value, match.Groups["diagnostic"].Value.Trim());
        }

        return value;
    }

    /// <summary>
    /// Localizes the invariant prefix used by generated percentage labels such as driver or image progress.
    /// </summary>
    /// <param name="label">The label text without the trailing percentage.</param>
    /// <returns>The localized label without a trailing ellipsis when a mapping exists.</returns>
    private static string LocalizeProgressLabel(string label)
    {
        string localized = LocalizeMessage(label);
        if (!localized.Equals(label, StringComparison.Ordinal))
        {
            return localized;
        }

        localized = LocalizeMessage($"{label}...");
        return localized.Equals($"{label}...", StringComparison.Ordinal)
            ? label
            : TrimTrailingEllipsis(localized);
    }

    private static string TrimTrailingEllipsis(string value)
    {
        return value.EndsWith("...", StringComparison.Ordinal)
            ? value[..^3]
            : value;
    }

    private static string LocalizeCompactRemainingDuration(string value)
    {
        Match match = RemainingDurationRegex().Match(value);
        return match.Success
            ? $"{match.Groups["value"].Value}s"
            : value;
    }

    private static bool TryLocalizeSingleSuffix(string value, string prefix, string key, out string localized)
    {
        if (value.StartsWith(prefix, StringComparison.Ordinal))
        {
            localized = LocalizationText.Format(key, value[prefix.Length..].Trim());
            return true;
        }

        localized = string.Empty;
        return false;
    }

    [GeneratedRegex(@"^Catalogs loaded: (?<os>\d+) OS entries, (?<drivers>\d+) driver packs\.$")]
    private static partial Regex CatalogLoadedRegex();

    [GeneratedRegex(@"^Windows optional features configured \((?<changed>\d+) changed, (?<satisfied>\d+) already satisfied, (?<unavailable>\d+) unavailable\)\.$")]
    private static partial Regex WindowsOptionalFeatureConfiguredRegex();

    [GeneratedRegex(@"^Windows optional feature configuration simulated \((?<actions>\d+) actions\)\.$")]
    private static partial Regex WindowsOptionalFeatureSimulationRegex();

    [GeneratedRegex(@"^Windows optional feature '(?<feature>.+)' has a removed payload and no supported local source mapping\.$")]
    private static partial Regex WindowsOptionalFeatureRemovedPayloadRegex();

    [GeneratedRegex(@"^Windows optional feature verification failed for '(?<feature>.+)'\.$")]
    private static partial Regex WindowsOptionalFeatureVerificationFailedRegex();

    [GeneratedRegex(@"^Target disk (?<disk>\d+) is no longer present\.$")]
    private static partial Regex TargetDiskMissingRegex();

    [GeneratedRegex(@"^Target disk (?<disk>\d+) is blocked: (?<warning>.+)$")]
    private static partial Regex TargetDiskBlockedRegex();

    [GeneratedRegex(@"^(?<label>.+): (?<percent>\d+(?:[.,]\d+)?)%$")]
    private static partial Regex StepPercentLabelRegex();

    [GeneratedRegex(@"^(?<size>.+) downloaded$")]
    private static partial Regex DownloadedBytesRegex();

    [GeneratedRegex(@"^Step: (?<current>\d+) of (?<total>\d+)$")]
    private static partial Regex StepCounterRegex();

    [GeneratedRegex(@"^Starting (?<step>.+)\.$")]
    private static partial Regex StartingStepRegex();

    [GeneratedRegex(@"^Starting (?<step>.+)\.\.\.$")]
    private static partial Regex StartingStepSubProgressRegex();

    [GeneratedRegex(@"^(?<value>\d+) seconds?$")]
    private static partial Regex RemainingDurationRegex();

    [GeneratedRegex(@"^Checking Windows Autopilot devices \((?<remaining>.+) remaining\)\.\.\.$")]
    private static partial Regex CheckingWindowsAutopilotDevicesRegex();

    [GeneratedRegex(@"^Checking Windows Autopilot group tag \((?<remaining>.+) remaining\)\.\.\.$")]
    private static partial Regex CheckingWindowsAutopilotGroupTagRegex();

    [GeneratedRegex(@"^wpeutil\.exe failed with exit code (?<code>\d+)\. (?<diagnostic>.+)$")]
    private static partial Regex WpeUtilFailureRegex();
}
