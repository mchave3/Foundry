// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.Configuration;
using Foundry.Core.Services.WinPe;
using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Logging;

namespace Foundry.Deploy.Services.Deployment.Steps;

public sealed class ValidateTargetConfigurationStep : DeploymentStepBase
{
    private readonly IHardwareProfileService _hardwareProfileService;

    public ValidateTargetConfigurationStep(IHardwareProfileService hardwareProfileService)
    {
        _hardwareProfileService = hardwareProfileService;
    }

    public override string Name => DeploymentStepNames.ValidateTargetConfiguration;

    protected override async Task<DeploymentStepResult> ExecuteLiveAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        DeploymentStepResult? optionalFeatureFailure = await ValidateOptionalFeaturesAsync(context, cancellationToken).ConfigureAwait(false);
        if (optionalFeatureFailure is not null)
        {
            return optionalFeatureFailure;
        }

        context.EmitCurrentStepIndeterminate(
            "Validating target configuration...",
            "Revalidating target disk...",
            DeploymentOperationNames.ValidateTargetDisk);
        (_, DeploymentStepResult? validationFailure) = await context.TryGetValidatedTargetDiskAsync(cancellationToken).ConfigureAwait(false);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        if (string.IsNullOrWhiteSpace(context.Request.OperatingSystem.Url))
        {
            return DeploymentStepResult.Failed(
                "Operating system URL is missing.",
                DeploymentFailure.Guard(
                    DeploymentOperationNames.ValidateTarget,
                    DeploymentFailureReasons.InvalidInput,
                    "missing_operating_system_url"));
        }

        if (context.Request.TargetDiskNumber < 0)
        {
            return DeploymentStepResult.Failed(
                "Target disk number is required.",
                DeploymentFailure.Guard(
                    DeploymentOperationNames.ValidateTargetDisk,
                    DeploymentFailureReasons.InvalidInput,
                    "missing_target_disk_number"));
        }

        context.EmitCurrentStepIndeterminate(
            "Validating target configuration...",
            "Detecting hardware profile...",
            DeploymentOperationNames.DetectHardware);
        HardwareProfile hardware = await _hardwareProfileService.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        context.RuntimeState.HardwareProfile = hardware;
        if (hardware.FirmwareType != Foundry.Utilities.Hardware.WindowsFirmwareType.Uefi)
        {
            return DeploymentStepResult.Failed("Deployment requires UEFI boot mode.",
                DeploymentFailure.Guard(DeploymentOperationNames.ValidateTarget, DeploymentFailureReasons.InvalidState, "unsupported_boot_firmware"));
        }
        await context.AppendLogAsync(DeploymentLogLevel.Info, $"Detected hardware: {hardware.DisplayLabel}", cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded("Target configuration validated.");
    }

    protected override async Task<DeploymentStepResult> ExecuteDryRunAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
    {
        DeploymentStepResult? optionalFeatureFailure = await ValidateOptionalFeaturesAsync(context, cancellationToken).ConfigureAwait(false);
        if (optionalFeatureFailure is not null)
        {
            return optionalFeatureFailure;
        }

        context.EmitCurrentStepIndeterminate(
            "Validating target configuration...",
            "Detecting hardware profile...",
            DeploymentOperationNames.DetectHardware);
        HardwareProfile hardware = await _hardwareProfileService.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        context.RuntimeState.HardwareProfile = hardware;
        await context.AppendLogAsync(DeploymentLogLevel.Info, $"[DRY-RUN] Hardware detected: {hardware.DisplayLabel}", cancellationToken).ConfigureAwait(false);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        return DeploymentStepResult.Succeeded("Target configuration validated (simulation).");
    }

    private static async Task<DeploymentStepResult?> ValidateOptionalFeaturesAsync(
        DeploymentStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        context.SetCurrentOperation(DeploymentOperationNames.ValidateWindowsOptionalFeatures);
        if (!WindowsOptionalFeatureActionValidator.TryNormalize(
            context.RuntimeState.WindowsOptionalFeatures,
            out DeployWindowsOptionalFeatureSettings normalized,
            out _))
        {
            return DeploymentStepResult.Failed(
                "Windows optional feature configuration is invalid.",
                new DeploymentFailure(
                    DeploymentOperationNames.ValidateWindowsOptionalFeatures,
                    DeploymentFailureKinds.Validation,
                    DeploymentFailureReasons.InvalidInput,
                    "optional_feature_configuration_invalid"));
        }

        context.RuntimeState.WindowsOptionalFeatures = normalized;
        if (!normalized.IsEnabled || normalized.Actions.Count == 0)
        {
            return null;
        }

        WinPeArchitecture architecture = context.Request.OperatingSystem.Architecture.Equals(
            "arm64",
            StringComparison.OrdinalIgnoreCase)
            ? WinPeArchitecture.Arm64
            : WinPeArchitecture.X64;
        foreach (DeployWindowsOptionalFeatureAction action in normalized.Actions.Where(action => action.Enable))
        {
            WindowsOptionalFeatureCompatibility compatibility = WindowsOptionalFeatureCompatibilityEvaluator.EvaluateBuilds(
                action.Id,
                [context.Request.OperatingSystem.Edition],
                [context.Request.OperatingSystem.BuildMajor],
                architecture);
            if (compatibility == WindowsOptionalFeatureCompatibility.Unavailable)
            {
                await context.AppendLogAsync(
                    DeploymentLogLevel.Warning,
                    $"Windows optional feature '{action.Id}' is documented as unavailable for the selected Windows target and will be verified against the applied image.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }
}
