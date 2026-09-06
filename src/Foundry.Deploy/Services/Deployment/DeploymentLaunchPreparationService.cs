// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.ApplicationShell;
using Foundry.Deploy.Services.Localization;
using ComputerNameRules = Foundry.Core.Services.Configuration.ComputerNameRules;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>
/// Validates launch selections and asks the shell for destructive deployment confirmation before creating a deployment context.
/// </summary>
public sealed class DeploymentLaunchPreparationService : IDeploymentLaunchPreparationService
{
    private readonly IApplicationShellService _applicationShellService;
    private readonly Unattend.UnattendContentService? _unattendContentService;

    public DeploymentLaunchPreparationService(IApplicationShellService applicationShellService, Unattend.UnattendContentService? unattendContentService = null)
    {
        _applicationShellService = applicationShellService;
        _unattendContentService = unattendContentService;
    }

    /// <summary>
    /// Builds a deployment context when the request is valid and the user confirms disk erasure.
    /// </summary>
    /// <param name="request">The wizard selections and launch options to validate.</param>
    /// <returns>The normalized launch result, including a deployment context when startup can continue.</returns>
    public DeploymentLaunchPreparationResult Prepare(DeploymentLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SelectedOperatingSystem is null)
        {
            return DeploymentLaunchPreparationResult.Failure(ComputerNameRules.Normalize(request.TargetComputerName));
        }

        string normalizedComputerName = ComputerNameRules.Normalize(request.TargetComputerName);
        if (!request.UsesCustomUnattend && !ComputerNameRules.IsValid(normalizedComputerName))
        {
            return DeploymentLaunchPreparationResult.Failure(normalizedComputerName);
        }

        bool hasCustomCommands = false;
        if (request.Unattend is not null)
        {
            try
            {
                if (_unattendContentService is null) return DeploymentLaunchPreparationResult.Failure(normalizedComputerName);
                using Unattend.UnattendSnapshot snapshot = _unattendContentService.Read(request.Unattend,
                    request.SelectedOperatingSystem.Architecture, request.IsAutopilotEnabled, request.AutopilotProvisioningMode);
                hasCustomCommands = snapshot.Inspection.HasCommands;
            }
            catch (Exception ex) when (ex is global::System.IO.InvalidDataException or global::System.IO.IOException or InvalidOperationException)
            {
                return DeploymentLaunchPreparationResult.Failure(normalizedComputerName, LocalizationText.GetString(
                    ex is InvalidOperationException ? "Unattend.AutopilotConflict" : "Unattend.Invalid"));
            }
        }

        TargetDiskInfo? effectiveTargetDisk = request.SelectedTargetDisk;
        if (effectiveTargetDisk is null && request.IsDryRun)
        {
            effectiveTargetDisk = TargetDiskInfoFactory.CreateDebugVirtualDisk();
        }

        if (effectiveTargetDisk is null)
        {
            return DeploymentLaunchPreparationResult.Failure(normalizedComputerName);
        }

        if (!request.IsDryRun && (!effectiveTargetDisk.IsSelectable || effectiveTargetDisk.IsSimulationOnly))
        {
            return DeploymentLaunchPreparationResult.Failure(normalizedComputerName);
        }

        if (request.DriverPackSelectionKind == DriverPackSelectionKind.OemCatalog &&
            request.SelectedDriverPack is null)
        {
            return DeploymentLaunchPreparationResult.Failure(normalizedComputerName);
        }

        if (request.IsAutopilotEnabled &&
            request.AutopilotProvisioningMode == AutopilotProvisioningMode.JsonProfile &&
            request.SelectedAutopilotProfile is null)
        {
            return DeploymentLaunchPreparationResult.Failure(normalizedComputerName);
        }

        if (!request.IsDryRun && !ConfirmDestructiveDeployment(effectiveTargetDisk, request.SelectedOperatingSystem, request, hasCustomCommands))
        {
            return DeploymentLaunchPreparationResult.Failure(normalizedComputerName);
        }

        DeploymentContext context = new()
        {
            Mode = request.Mode,
            CacheRootPath = request.CacheRootPath,
            TargetDiskNumber = effectiveTargetDisk.DiskNumber,
            ConfirmedTargetDisk = request.IsDryRun ? null : TargetDiskIdentity.FromDisk(effectiveTargetDisk),
            Unattend = request.Unattend,
            TargetComputerName = request.UsesCustomUnattend ? string.Empty : normalizedComputerName,
            DefaultTimeZoneId = request.UsesCustomUnattend || string.IsNullOrWhiteSpace(request.DefaultTimeZoneId) ? null : request.DefaultTimeZoneId.Trim(),
            OperatingSystem = request.SelectedOperatingSystem,
            DriverPackSelectionKind = request.DriverPackSelectionKind,
            DriverPack = request.SelectedDriverPack,
            ApplyFirmwareUpdates = request.ApplyFirmwareUpdates,
            IsAutopilotEnabled = request.IsAutopilotEnabled,
            AutopilotProvisioningMode = request.AutopilotProvisioningMode,
            SelectedAutopilotProfile = request.SelectedAutopilotProfile,
            AutopilotHardwareHashUpload = request.AutopilotHardwareHashUpload,
            Network = request.Network,
            Oobe = request.UsesCustomUnattend ? new DeployOobeSettings() : request.Oobe,
            AppxRemoval = request.AppxRemoval,
            AiComponentRemoval = request.AiComponentRemoval,
            WindowsOptionalFeatures = request.WindowsOptionalFeatures,
            Completion = request.Completion,
            IsDryRun = request.IsDryRun
        };

        return DeploymentLaunchPreparationResult.Success(
            normalizedComputerName,
            effectiveTargetDisk,
            context);
    }

    /// <summary>
    /// Shows the final warning that live deployments erase the selected target disk.
    /// </summary>
    /// <param name="targetDisk">The disk that will be repartitioned.</param>
    /// <param name="operatingSystem">The operating system image that will be applied.</param>
    /// <param name="request">Effective customization and answer-file ownership shown in the confirmation.</param>
    /// <param name="hasCustomCommands">Whether preserved commands require an overlap warning.</param>
    /// <returns><see langword="true"/> when the user confirms the destructive operation.</returns>
    private bool ConfirmDestructiveDeployment(TargetDiskInfo targetDisk, OperatingSystemCatalogItem operatingSystem, DeploymentLaunchRequest request, bool hasCustomCommands)
    {
        string sizeGiB = targetDisk.SizeBytes > 0
            ? $"{(targetDisk.SizeBytes / 1024d / 1024d / 1024d):0.0} GiB"
            : LocalizationText.GetString("Disk.UnknownSize");

        string message = LocalizationText.Format(
            "Launch.ConfirmDiskEraseMessageFormat",
            targetDisk.DiskNumber,
            targetDisk.FriendlyName,
            targetDisk.BusType,
            sizeGiB,
            operatingSystem.DisplayLabel);

        if (request.UsesCustomUnattend)
        {
            message += Environment.NewLine + Environment.NewLine + request.Unattend!.File.DisplayName + Environment.NewLine +
                LocalizationText.GetString("Unattend.Ownership") + Environment.NewLine +
                LocalizationText.GetString("Unattend.HookCompatibility");
            if (hasCustomCommands) message += Environment.NewLine + LocalizationText.GetString("Unattend.CommandsWarning");
            if (request.IsAutopilotEnabled && request.AutopilotProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload)
                message += Environment.NewLine + LocalizationText.GetString("Unattend.HashWarning");
        }

        return _applicationShellService.ConfirmWarning(LocalizationText.GetString("Launch.ConfirmDiskEraseTitle"), message);
    }
}
