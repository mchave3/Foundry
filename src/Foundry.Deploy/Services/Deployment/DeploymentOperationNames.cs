// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Services.Deployment;

/// <summary>
/// Defines stable logical deployment operation names used by telemetry.
/// </summary>
public static class DeploymentOperationNames
{
    public const string ValidateCustomUnattend = "unattend.validate";
    public const string StageCustomUnattend = "unattend.stage";
    public const string AcquireGate = "deployment.acquire_gate";
    public const string GatherVariables = "deployment.gather_variables";
    public const string InitializeWorkspace = "deployment.initialize_workspace";
    public const string WriteLogs = "deployment.write_logs";
    public const string WriteSummary = "deployment.write_summary";
    public const string CleanupWorkspace = "deployment.cleanup_workspace";
    public const string ValidateTarget = "target.validate";
    public const string ValidateTargetDisk = "target_disk.validate";
    public const string DetectHardware = "hardware.detect";
    public const string ResolveCache = "cache.resolve";
    public const string ValidateCacheTargetDisk = "cache.validate_target_disk";
    public const string PrepareTargetDisk = "target_disk.prepare";
    public const string PartitionTargetDisk = "target_disk.partition";
    public const string PrepareTargetWorkspace = "target_workspace.prepare";
    public const string DownloadOperatingSystemImage = "os_image.download";
    public const string InspectOperatingSystemImage = "os_image.inspect";
    public const string DownloadDriverPack = "driver_pack.download";
    public const string ResolveDriverPack = "driver_pack.resolve";
    public const string ExtractDriverPack = "driver_pack.extract";
    public const string ApplyOperatingSystemImage = "os_image.apply";
    public const string ConfigureBoot = "boot.configure";
    public const string VerifyOperatingSystemEdition = "os_image.verify_edition";
    public const string ConfigureComputerName = "computer_name.configure";
    public const string ConfigureOobe = "oobe.configure";
    public const string WriteOobeUnattend = "oobe.write_unattend";
    public const string WriteOobeRegistry = "oobe.write_registry";
    public const string WriteAiPolicyRegistry = "ai_policy.write_registry";
    public const string ValidateWindowsOptionalFeatures = "windows_optional_features.validate";
    public const string InspectWindowsOptionalFeatures = "windows_optional_features.inspect";
    public const string PrepareWindowsOptionalFeatureSource = "windows_optional_features.prepare_source";
    public const string ConfigureWindowsOptionalFeatures = "windows_optional_features.configure";
    public const string ConfigureRecovery = "recovery.configure";
    public const string StagePreOobe = "pre_oobe.stage";
    public const string ApplyDriverPack = "driver_pack.apply";
    public const string ApplyRecoveryDrivers = "driver_pack.apply_recovery";
    public const string MountRecoveryImage = "recovery.mount";
    public const string UnmountRecoveryImage = "recovery.unmount";
    public const string StageDeferredDriverPack = "driver_pack.stage_deferred";
    public const string DownloadFirmware = "firmware.download";
    public const string ResolveFirmware = "firmware.resolve";
    public const string ApplyFirmware = "firmware.apply";
    public const string SealRecovery = "recovery.seal";
    public const string ProvisionAutopilot = "autopilot.provision";
    public const string StageAutopilotProfile = "autopilot.stage_profile";
    public const string StageAutopilotAssistant = "autopilot.stage_assistant";
    public const string CaptureAutopilotHash = "autopilot.capture_hash";
    public const string UploadAutopilotHash = "autopilot.upload_hash";
    public const string WriteAutopilotManifest = "autopilot.write_manifest";
    public const string FinalizeDeployment = "deployment.finalize";

    /// <summary>
    /// Resolves a stable fallback operation for a deployment step.
    /// </summary>
    /// <param name="stepName">Canonical deployment step name.</param>
    /// <returns>Stable logical operation name.</returns>
    public static string ForStep(string stepName)
    {
        return stepName switch
        {
            DeploymentStepNames.ValidateCustomUnattend => ValidateCustomUnattend,
            DeploymentStepNames.StageCustomUnattend => StageCustomUnattend,
            DeploymentStepNames.GatherDeploymentVariables => GatherVariables,
            DeploymentStepNames.InitializeDeploymentWorkspace => InitializeWorkspace,
            DeploymentStepNames.ValidateTargetConfiguration => ValidateTarget,
            DeploymentStepNames.ResolveCacheStrategy => ResolveCache,
            DeploymentStepNames.PrepareTargetDiskLayout => PrepareTargetDisk,
            DeploymentStepNames.DownloadOperatingSystemImage => DownloadOperatingSystemImage,
            DeploymentStepNames.DownloadDriverPack => DownloadDriverPack,
            DeploymentStepNames.ExtractDriverPack => ExtractDriverPack,
            DeploymentStepNames.ApplyOperatingSystemImage => ApplyOperatingSystemImage,
            DeploymentStepNames.ConfigureTargetComputerName => ConfigureComputerName,
            DeploymentStepNames.ConfigureOobeSettings => ConfigureOobe,
            DeploymentStepNames.ConfigureWindowsOptionalFeatures => ConfigureWindowsOptionalFeatures,
            DeploymentStepNames.ConfigureRecoveryEnvironment => ConfigureRecovery,
            DeploymentStepNames.StagePreOobeCustomization => StagePreOobe,
            DeploymentStepNames.ApplyDriverPack => ApplyDriverPack,
            DeploymentStepNames.ApplyRecoveryDrivers => ApplyRecoveryDrivers,
            DeploymentStepNames.DownloadFirmwareUpdate => DownloadFirmware,
            DeploymentStepNames.ApplyFirmwareUpdate => ApplyFirmware,
            DeploymentStepNames.SealRecoveryPartition => SealRecovery,
            DeploymentStepNames.ProvisionAutopilot => ProvisionAutopilot,
            DeploymentStepNames.FinalizeDeploymentAndWriteLogs => FinalizeDeployment,
            _ => "deployment.unknown"
        };
    }
}
