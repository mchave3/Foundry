// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Catalog;
using Foundry.Deploy.Services.Startup;
using Foundry.Deploy.ViewModels;
using CoreDeployNetworkSettings = Foundry.Core.Models.Configuration.Deploy.DeployNetworkSettings;

namespace Foundry.Deploy.Services.Wizard;

public sealed class DeploymentWizardContext : IDisposable
{
    private bool _isDisposed;

    public DeploymentWizardContext(
        DeploymentPreparationViewModel preparation,
        OperatingSystemCatalogViewModel operatingSystemCatalog,
        DriverPackSelectionViewModel driverPackSelection)
    {
        Preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        OperatingSystemCatalog = operatingSystemCatalog ?? throw new ArgumentNullException(nameof(operatingSystemCatalog));
        DriverPackSelection = driverPackSelection ?? throw new ArgumentNullException(nameof(driverPackSelection));

        Preparation.StateChanged += OnPreparationStateChanged;
        OperatingSystemCatalog.StateChanged += OnOperatingSystemCatalogStateChanged;
        DriverPackSelection.StateChanged += OnDriverPackSelectionStateChanged;

        RefreshDriverPackSelectionContext();
    }

    public DeploymentPreparationViewModel Preparation { get; }
    public OperatingSystemCatalogViewModel OperatingSystemCatalog { get; }
    public DriverPackSelectionViewModel DriverPackSelection { get; }
    public string? DefaultTimeZoneId { get; private set; }
    public DeployCompletionSettings Completion { get; private set; } = new();
    public CoreDeployNetworkSettings Network { get; private set; } = new();
    public DeployOobeSettings Oobe { get; private set; } = new();
    public DeployAppxRemovalSettings AppxRemoval { get; private set; } = new();
    public DeployAiComponentRemovalSettings AiComponentRemoval { get; private set; } = new();
    public DeployWindowsOptionalFeatureSettings WindowsOptionalFeatures { get; private set; } = new();

    public event EventHandler? StateChanged;

    public void ApplyStartupSnapshot(DeploymentStartupSnapshot startupSnapshot)
    {
        ArgumentNullException.ThrowIfNull(startupSnapshot);

        Preparation.CacheRootPath = startupSnapshot.CacheRootPath;

        if (startupSnapshot.DeployConfigurationDocument is not null)
        {
            ApplyDeployConfiguration(
                startupSnapshot.DeployConfigurationDocument,
                startupSnapshot.AutopilotProfiles);
        }
        else
        {
            Preparation.ApplyAutopilotConfiguration(new DeployAutopilotSettings(), startupSnapshot.AutopilotProfiles);
        }

        Preparation.ApplyUnattendConfiguration(
            startupSnapshot.DeployConfigurationDocument?.Unattend ?? new(),
            startupSnapshot.ConfigurationPath,
            startupSnapshot.ConfigurationFailureMessage);
        Preparation.ApplyMachineNamePreparation(startupSnapshot.MachineNamePreparation);

        if (startupSnapshot.DetectedHardware is not null)
        {
            Preparation.SetDetectedHardware(startupSnapshot.DetectedHardware);
            OperatingSystemCatalog.SetEffectiveArchitecture(startupSnapshot.DetectedHardware.Architecture);
        }
        else if (!string.IsNullOrWhiteSpace(startupSnapshot.HardwareDetectionFailureMessage))
        {
            Preparation.SetHardwareDetectionFailure(startupSnapshot.HardwareDetectionFailureMessage);
        }

        Preparation.ApplyTargetDisks(startupSnapshot.TargetDisks);
        ApplyCatalogSnapshot(startupSnapshot.CatalogSnapshot);
    }

    public void ApplyCatalogSnapshot(DeploymentCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        OperatingSystemCatalog.ApplyCatalog(snapshot.OperatingSystems);
        DriverPackSelection.ReplaceCatalog(snapshot.DriverPacks);
        RefreshDriverPackSelectionContext();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Preparation.StateChanged -= OnPreparationStateChanged;
        OperatingSystemCatalog.StateChanged -= OnOperatingSystemCatalogStateChanged;
        DriverPackSelection.StateChanged -= OnDriverPackSelectionStateChanged;
        Preparation.Dispose();
        DriverPackSelection.Dispose();
        _isDisposed = true;
    }

    private void ApplyDeployConfiguration(
        FoundryDeployConfigurationDocument document,
        IReadOnlyList<AutopilotProfileCatalogItem> autopilotProfiles)
    {
        Completion = document.Completion ?? new DeployCompletionSettings();
        OperatingSystemCatalog.ApplyOperatingSystemSelection(document.OperatingSystemSelection);
        DefaultTimeZoneId = string.IsNullOrWhiteSpace(document.Localization.DefaultTimeZoneId)
            ? null
            : document.Localization.DefaultTimeZoneId.Trim();
        Preparation.ApplyMachineNamingConfiguration(
            document.Customization.MachineNaming ?? new DeployMachineNamingSettings());
        Network = document.Network ?? new CoreDeployNetworkSettings();
        Oobe = document.Customization.Oobe ?? new DeployOobeSettings();
        AppxRemoval = document.Customization.AppxRemoval ?? new DeployAppxRemovalSettings();
        AiComponentRemoval = document.Customization.AiComponentRemoval ?? new DeployAiComponentRemovalSettings();
        WindowsOptionalFeatures = document.Customization.WindowsOptionalFeatures ?? new DeployWindowsOptionalFeatureSettings();
        Preparation.ApplyAutopilotConfiguration(document.Autopilot ?? new DeployAutopilotSettings(), autopilotProfiles);
    }

    private void OnPreparationStateChanged(object? sender, EventArgs e)
    {
        RefreshDriverPackSelectionContext();

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOperatingSystemCatalogStateChanged(object? sender, EventArgs e)
    {
        RefreshDriverPackSelectionContext();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDriverPackSelectionStateChanged(object? sender, EventArgs e)
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshDriverPackSelectionContext()
    {
        Preparation.UpdateUnattendContext(OperatingSystemCatalog.SelectedOperatingSystem?.Architecture ?? OperatingSystemCatalog.EffectiveOsArchitecture);
        DriverPackSelection.UpdateSelectionContext(
            Preparation.DetectedHardware,
            OperatingSystemCatalog.SelectedOperatingSystem,
            OperatingSystemCatalog.EffectiveOsArchitecture);
    }
}
