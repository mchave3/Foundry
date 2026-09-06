// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.ApplicationShell;
using Foundry.Deploy.Services.Autopilot;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Catalog;
using Foundry.Deploy.Services.Configuration;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Deployment.PreOobe;
using Foundry.Deploy.Services.Deployment.Steps;
using Foundry.Deploy.Services.Network;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Localization;
using Foundry.Deploy.Services.Operations;
using Foundry.Deploy.Services.Runtime;
using Foundry.Deploy.Services.Security;
using Foundry.Deploy.Services.Startup;
using Foundry.Deploy.Services.System;
using Foundry.Deploy.Services.Theme;
using Foundry.Deploy.Services.Wizard;
using Foundry.Deploy.ViewModels;
using Foundry.Telemetry;
using Foundry.Utilities.Hardware;
using Foundry.Utilities.Networking;
using Foundry.Utilities.Runtime;
using Foundry.Utilities.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UtilityProcessRunner = Foundry.Utilities.Processes.ProcessRunner;

namespace Foundry.Deploy.DependencyInjection;

public static class ServiceCollectionExtensions
{
    private const string DeploymentModeEnvironmentVariable = "FOUNDRY_DEPLOYMENT_MODE";

    public static IServiceCollection AddFoundryDeployApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<App>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();

        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IApplicationShellService, ApplicationShellService>();
        services.AddSingleton<IDeploymentWizardStateService, DeploymentWizardStateService>();
        services.AddSingleton<IDeploymentWizardContextFactory, DeploymentWizardContextFactory>();
        services.AddSingleton<IDeploymentStartupCoordinator, DeploymentStartupCoordinator>();
        services.AddSingleton<IOperationProgressService, OperationProgressService>();
        services.AddSingleton<IVolumeDiscovery, WindowsVolumeDiscovery>();
        services.AddSingleton<IDeploymentRuntimeContextService, DeploymentRuntimeContextService>();
        services.AddSingleton<IDeployConfigurationService, DeployConfigurationService>();
        services.AddSingleton<IDeploymentSecretKeySession, DeploymentSecretKeySession>();
        services.AddSingleton<IDeploymentSecretKeyProvider, DeploymentSecretKeyProvider>();
        services.AddSingleton<IDeploymentProtectionUnlockService, DeploymentProtectionUnlockService>();
        services.AddSingleton<IDeploymentPasswordDialogService, DeploymentPasswordDialogService>();
        services.AddSingleton<IDeploymentAccessRetryDelay, DeploymentAccessRetryDelay>();
        services.AddSingleton<IDeploymentAccessGate, DeploymentAccessGate>();
        services.AddSingleton(LoadTelemetrySettings);
        services.AddSingleton(CreateTelemetryOptions);
        services.AddSingleton(CreateTelemetryContext);
        services.AddSingleton<IRemoteDiagnosticsService>(_ => new PostHogRemoteDiagnosticsSink());
        services.AddSingleton<ITelemetryService>(sp =>
        {
            TelemetryOptions options = sp.GetRequiredService<TelemetryOptions>();
            ILogger<PostHogTelemetryService> logger = sp.GetRequiredService<ILogger<PostHogTelemetryService>>();
            logger.LogDebug(
                "Configuring telemetry service. App={App}, IsEnabled={IsEnabled}, HasProjectToken={HasProjectToken}, HasInstallId={HasInstallId}, HostUrl={HostUrl}.",
                TelemetryApps.FoundryDeploy,
                options.IsEnabled,
                !string.IsNullOrWhiteSpace(options.ProjectToken),
                !string.IsNullOrWhiteSpace(options.InstallId),
                options.HostUrl);

            if (!options.CanSend)
            {
                logger.LogDebug("Telemetry service disabled for Foundry.Deploy because runtime options are incomplete or disabled.");
                return new NullTelemetryService();
            }

            return new PostHogTelemetryService(
                new HttpClient(),
                options,
                sp.GetRequiredService<TelemetryContext>(),
                logger);
        });
        services.AddSingleton<IDeploymentLaunchPreparationService, DeploymentLaunchPreparationService>();
        services.AddSingleton<IDeploymentExecutionService, DeploymentExecutionService>();
        services.AddSingleton<UtilityProcessRunner>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IArchiveExtractionService, ArchiveExtractionService>();
        services.AddSingleton<ICacheLocatorService, CacheLocatorService>();
        services.AddSingleton<IDeploymentLogService, DeploymentLogService>();
        services.AddSingleton<IHardwareInspector, WindowsHardwareInspector>();
        services.AddSingleton<IHardwareProfileService, HardwareProfileService>();
        services.AddSingleton<INetworkAdapterSnapshotProvider, WindowsNetworkAdapterSnapshotProvider>();
        services.AddSingleton<IOfflineWindowsComputerNameService, OfflineWindowsComputerNameService>();
        services.AddSingleton<IWindowsDiskInspector, WindowsDiskInspector>();
        services.AddSingleton<ITargetDiskService, TargetDiskService>();
        services.AddSingleton<IOperatingSystemCatalogService, OperatingSystemCatalogService>();
        services.AddSingleton<IDriverPackCatalogService, DriverPackCatalogService>();
        services.AddSingleton<IDeploymentCatalogLoadService, DeploymentCatalogLoadService>();
        services.AddSingleton<IDriverPackSelectionService, DriverPackSelectionService>();
        services.AddSingleton<IMicrosoftUpdateCatalogClient, MicrosoftUpdateCatalogClient>();
        services.AddSingleton<IMicrosoftUpdateCatalogDriverService, MicrosoftUpdateCatalogDriverService>();
        services.AddSingleton<IMicrosoftUpdateCatalogFirmwareService, MicrosoftUpdateCatalogFirmwareService>();
        services.AddSingleton<IArtifactDownloadService, ArtifactDownloadService>();
        services.AddSingleton<IDriverPackStrategyResolver, DriverPackStrategyResolver>();
        services.AddSingleton<IDriverPackExtractionService, DriverPackExtractionService>();
        services.AddSingleton<IWindowsDeploymentService, WindowsDeploymentService>();
        services.AddSingleton<ISetupCompleteScriptService, SetupCompleteScriptService>();
        services.AddSingleton<IPreOobeScriptProvisioningService, PreOobeScriptProvisioningService>();
        services.AddSingleton<PreOobeScriptDefinitionBuilder>();
        services.AddSingleton<INetworkProfileRoamingArtifactService, NetworkProfileRoamingArtifactService>();
        services.AddSingleton<IAutopilotProfileContentService, AutopilotProfileContentService>();
        services.AddSingleton<IAutopilotProfileCatalogService, AutopilotProfileCatalogService>();
        services.AddSingleton<IAutopilotHardwareHashCaptureService, AutopilotHardwareHashCaptureService>();
        services.AddSingleton<IAutopilotInteractiveRegistrationProvisioningService, AutopilotInteractiveRegistrationProvisioningService>();
        services.AddSingleton<INetworkSecretKeyReader, NetworkSecretKeyReader>();
        services.AddSingleton<IAutopilotGraphTokenService>(sp =>
            new AutopilotGraphTokenService(
                new HttpClient(),
                sp.GetRequiredService<ILogger<AutopilotGraphTokenService>>()));
        services.AddSingleton(sp =>
            new AutopilotGraphImportClient(
                new HttpClient
                {
                    BaseAddress = new Uri("https://graph.microsoft.com/", UriKind.Absolute)
                },
                sp.GetRequiredService<ILogger<AutopilotGraphImportClient>>()));
        services.AddSingleton<IAutopilotGroupTagDiscoveryService, AutopilotGroupTagDiscoveryService>();
        services.AddSingleton<IAutopilotHardwareHashUploadService, AutopilotHardwareHashUploadService>();
        // Runtime execution follows DeploymentStepNames.ExecutionOrder, not registration order.
        services.AddSingleton<IDeploymentStep, ApplyDriverPackStep>();
        services.AddSingleton<IDeploymentStep, ApplyFirmwareUpdateStep>();
        services.AddSingleton<IDeploymentStep, ApplyOperatingSystemImageStep>();
        services.AddSingleton<IDeploymentStep, ApplyRecoveryDriversStep>();
        services.AddSingleton<IDeploymentStep, ConfigureOobeSettingsStep>();
        services.AddSingleton<IDeploymentStep, ConfigureRecoveryEnvironmentStep>();
        services.AddSingleton<Foundry.Deploy.Services.Deployment.Unattend.UnattendContentService>();
        services.AddSingleton<IDeploymentStep, ValidateCustomUnattendStep>();
        services.AddSingleton<IDeploymentStep, StageCustomUnattendStep>();
        services.AddSingleton<IDeploymentStep, ConfigureTargetComputerNameStep>();
        services.AddSingleton<IDeploymentStep, ConfigureWindowsOptionalFeaturesStep>();
        services.AddSingleton<IDeploymentStep, DownloadDriverPackStep>();
        services.AddSingleton<IDeploymentStep, DownloadFirmwareUpdateStep>();
        services.AddSingleton<IDeploymentStep, DownloadOperatingSystemImageStep>();
        services.AddSingleton<IDeploymentStep, ExtractDriverPackStep>();
        services.AddSingleton<IDeploymentStep, FinalizeDeploymentAndWriteLogsStep>();
        services.AddSingleton<IDeploymentStep, GatherDeploymentVariablesStep>();
        services.AddSingleton<IDeploymentStep, InitializeDeploymentWorkspaceStep>();
        services.AddSingleton<IDeploymentStep, PrepareTargetDiskLayoutStep>();
        services.AddSingleton<IDeploymentStep, ProvisionAutopilotStep>();
        services.AddSingleton<IDeploymentStep, ResolveCacheStrategyStep>();
        services.AddSingleton<IDeploymentStep, SealRecoveryPartitionStep>();
        services.AddSingleton<IDeploymentStep, StagePreOobeCustomizationStep>();
        services.AddSingleton<IDeploymentStep, ValidateTargetConfigurationStep>();
        services.AddSingleton<IDeploymentOrchestrator, DeploymentOrchestrator>();

        return services;
    }

    private static TelemetryOptions CreateTelemetryOptions(IServiceProvider serviceProvider)
    {
        TelemetrySettings settings = serviceProvider.GetRequiredService<TelemetrySettings>();
        return new TelemetryOptions(
            settings.IsEnabled,
            string.IsNullOrWhiteSpace(settings.HostUrl) ? TelemetryDefaults.PostHogEuHost : settings.HostUrl,
            string.IsNullOrWhiteSpace(settings.ProjectToken) ? TelemetryDefaults.ProjectToken : settings.ProjectToken,
            settings.InstallId);
    }

    private static TelemetryContext CreateTelemetryContext(IServiceProvider serviceProvider)
    {
        TelemetrySettings settings = serviceProvider.GetRequiredService<TelemetrySettings>();
        string runtime = WinPeRuntimeDetector.IsWinPeRuntime() ? TelemetryRuntimeModes.WinPe : TelemetryRuntimeModes.Desktop;
        return TelemetryContextFactory.Create(
            TelemetryApps.FoundryDeploy,
            FoundryDeployApplicationInfo.Version,
            TelemetryBuildConfiguration.Current,
            runtime,
            string.IsNullOrWhiteSpace(settings.RuntimePayloadSource)
                ? TelemetryRuntimePayloadSources.Unknown
                : settings.RuntimePayloadSource,
            TelemetryBootMediaTargetResolver.Resolve(
                runtime,
                Environment.GetEnvironmentVariable(DeploymentModeEnvironmentVariable)),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            CultureInfo.CurrentUICulture.Name);
    }

    private static TelemetrySettings LoadTelemetrySettings(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<IDeployConfigurationService>()
            .LoadOptional()
            .Document
            ?.Telemetry ?? new TelemetrySettings();
    }
}
