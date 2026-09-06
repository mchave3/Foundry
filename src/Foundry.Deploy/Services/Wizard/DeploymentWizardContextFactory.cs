// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.Localization;
using Foundry.Deploy.ViewModels;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Wizard;

public sealed class DeploymentWizardContextFactory : IDeploymentWizardContextFactory
{
    private readonly IDriverPackSelectionService _driverPackSelectionService;
    private readonly ILocalizationService _localizationService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Foundry.Deploy.Services.Deployment.Unattend.UnattendContentService _unattendContentService;

    public DeploymentWizardContextFactory(
        IDriverPackSelectionService driverPackSelectionService,
        ILocalizationService localizationService,
        ILoggerFactory loggerFactory,
        Foundry.Deploy.Services.Deployment.Unattend.UnattendContentService unattendContentService)
    {
        _driverPackSelectionService = driverPackSelectionService;
        _localizationService = localizationService;
        _loggerFactory = loggerFactory;
        _unattendContentService = unattendContentService;
    }

    public DeploymentWizardContext Create(bool isDebugSafeMode)
    {
        DeploymentPreparationViewModel preparation = new(
            _localizationService,
            isDebugSafeMode,
            _unattendContentService);
        OperatingSystemCatalogViewModel operatingSystemCatalog = new(
            _loggerFactory.CreateLogger<OperatingSystemCatalogViewModel>(),
            Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty);
        DriverPackSelectionViewModel driverPackSelection = new(
            _driverPackSelectionService,
            _localizationService,
            operatingSystemCatalog.EffectiveOsArchitecture);

        return new DeploymentWizardContext(
            preparation,
            operatingSystemCatalog,
            driverPackSelection);
    }
}
