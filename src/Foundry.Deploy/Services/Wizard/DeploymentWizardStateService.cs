// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Services.Wizard;

public sealed class DeploymentWizardStateService : IDeploymentWizardStateService
{
    public bool CanGoPrevious(DeploymentWizardStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return !snapshot.IsDeploymentRunning && GetCurrentStepIndex(snapshot) > 0;
    }

    public bool CanGoNext(DeploymentWizardStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.IsDeploymentRunning || snapshot.CurrentStepId == DeploymentWizardStepId.Summary)
        {
            return false;
        }

        if (snapshot.CurrentStepId == DeploymentWizardStepId.TargetDevice)
        {
            return !snapshot.IsCatalogLoading &&
                   snapshot.IsOperatingSystemCatalogReadyForNavigation && snapshot.IsUnattendSelectionValid;
        }

        if (snapshot.CurrentStepId == DeploymentWizardStepId.Autopilot)
        {
            return snapshot.HasValidAutopilotSelection;
        }

        return true;
    }

    public bool CanStartDeployment(DeploymentWizardStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool hasTargetDisk = snapshot.HasTargetDiskSelection &&
                             (snapshot.IsDebugSafeMode || snapshot.IsSelectedTargetDiskSelectable);

        if (snapshot.IsDebugSafeMode && !snapshot.HasTargetDiskSelection)
        {
            hasTargetDisk = true;
        }

        return !snapshot.IsDeploymentRunning &&
               !snapshot.IsCatalogLoading &&
               !snapshot.IsTargetDiskLoading &&
               snapshot.CurrentStepId == DeploymentWizardStepId.Summary &&
               snapshot.IsTargetComputerNameValid &&
               snapshot.IsUnattendSelectionValid &&
               snapshot.HasSelectedOperatingSystem &&
               hasTargetDisk &&
               snapshot.HasValidDriverPackSelection &&
               snapshot.HasValidAutopilotSelection;
    }

    private static int GetCurrentStepIndex(DeploymentWizardStateSnapshot snapshot)
    {
        for (int index = 0; index < snapshot.AvailableSteps.Count; index++)
        {
            if (snapshot.AvailableSteps[index].Id == snapshot.CurrentStepId)
            {
                return index;
            }
        }

        return -1;
    }
}
