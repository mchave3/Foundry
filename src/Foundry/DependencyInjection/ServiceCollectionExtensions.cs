// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.Application;
using Foundry.Core.Services.Adk;
using Foundry.Core.Services.Configuration;
using Foundry.Core.Services.WinPe;
using Foundry.Services.Application;
using Foundry.Services.Appearance;
using Foundry.Services.Adk;
using Foundry.Services.Autopilot;
using Foundry.Services.Configuration;
using Foundry.Services.GitHub;
using Foundry.Services.Localization;
using Foundry.Services.Networking;
using Foundry.Services.Operations;
using Foundry.Services.Settings;
using Foundry.Services.Shell;
using Foundry.Services.Startup;
using Foundry.Services.Updates;
using Foundry.Telemetry;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Foundry.DependencyInjection;

/// <summary>
/// Registers the Foundry WinUI composition root.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds application services, view models, shell services, and Core service integrations.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddFoundryApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton(Log.Logger);

        services.AddSingleton<MainWindow>();

        services.AddSingleton<IAppSettingsService, JsonAppSettingsService>();
        services.AddSingleton<IProxyCredentialStore, ProxyCredentialStore>();
        services.AddSingleton<IApplicationProxyService, ApplicationProxyService>();
        services.AddSingleton(sp =>
        {
            FoundryAppSettings settings = sp.GetRequiredService<IAppSettingsService>().Current;
            return new TelemetrySettings
            {
                IsEnabled = settings.Telemetry.IsEnabled,
                IsRemoteDiagnosticsEnabled = settings.Telemetry.IsRemoteDiagnosticsEnabled,
                HostUrl = TelemetryDefaults.PostHogEuHost,
                ProjectToken = TelemetryDefaults.ProjectToken,
                InstallId = settings.Telemetry.InstallId,
                RuntimePayloadSource = TelemetryRuntimePayloadSources.None
            };
        });
        services.AddSingleton(sp =>
        {
            TelemetrySettings settings = sp.GetRequiredService<TelemetrySettings>();
            return new TelemetryOptions(
                settings.IsEnabled,
                settings.HostUrl,
                settings.ProjectToken,
                settings.InstallId);
        });
        services.AddSingleton(_ => TelemetryContextFactory.Create(
            TelemetryApps.FoundryOsd,
            FoundryApplicationInfo.Version,
            TelemetryBuildConfiguration.Current,
            TelemetryRuntimeModes.Desktop,
            TelemetryRuntimePayloadSources.None,
            TelemetryBootMediaTargets.None,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            CultureInfo.CurrentUICulture.Name));
        services.AddSingleton<ITelemetryService>(sp =>
        {
            TelemetryOptions options = sp.GetRequiredService<TelemetryOptions>();
            Microsoft.Extensions.Logging.ILogger<PostHogTelemetryService> logger =
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PostHogTelemetryService>>();
            logger.LogDebug(
                "Configuring telemetry service. App={App}, IsEnabled={IsEnabled}, HasProjectToken={HasProjectToken}, HasInstallId={HasInstallId}, HostUrl={HostUrl}.",
                TelemetryApps.FoundryOsd,
                options.IsEnabled,
                !string.IsNullOrWhiteSpace(options.ProjectToken),
                !string.IsNullOrWhiteSpace(options.InstallId),
                options.HostUrl);

            if (!options.CanSend)
            {
                logger.LogDebug("Telemetry service disabled for Foundry because runtime options are incomplete or disabled.");
                return new NullTelemetryService();
            }

            return new PostHogTelemetryService(
                new HttpClient(),
                options,
                sp.GetRequiredService<TelemetryContext>(),
                logger);
        });
        services.AddSingleton<IRemoteDiagnosticsService>(_ => new PostHogRemoteDiagnosticsSink());
        services.AddSingleton<IAdkInstallationProbe, WindowsAdkInstallationProbe>();
        services.AddSingleton<IFoundryConfigurationService, FoundryConfigurationService>();
        services.AddSingleton<IDeployConfigurationGenerator, DeployConfigurationGenerator>();
        services.AddSingleton<IConnectConfigurationGenerator, ConnectConfigurationGenerator>();
        services.AddSingleton<IAutopilotProfileImportService, AutopilotProfileImportService>();
        services.AddSingleton<IAutopilotTenantProfileService, AutopilotTenantProfileService>();
        services.AddSingleton<IAutopilotHardwareHashGraphSessionService, AutopilotHardwareHashGraphSessionService>();
        services.AddSingleton<IAutopilotTenantOnboardingService, AutopilotTenantOnboardingService>();
        services.AddSingleton<IAutopilotTenantOperationDialogService, AutopilotTenantOperationDialogService>();
        services.AddSingleton<IAutopilotCertificateDialogService, AutopilotCertificateDialogService>();
        services.AddSingleton<IAutopilotProfileSelectionDialogService, AutopilotProfileSelectionDialogService>();
        services.AddSingleton<IAutopilotHardwareHashSessionState, AutopilotHardwareHashSessionState>();
        services.AddSingleton<ILanguageRegistryService, EmbeddedLanguageRegistryService>();
        services.AddSingleton<INetworkSecretStateService, NetworkSecretStateService>();
        services.AddSingleton<IDeploymentProtectionSecretStateService, DeploymentProtectionSecretStateService>();
        services.AddSingleton<IOobeAccountSecretStateService, OobeAccountSecretStateService>();
        services.AddSingleton<IOobeAdditionalAccountDialogService, OobeAdditionalAccountDialogService>();
        services.AddSingleton<IFoundryConfigurationStateService, FoundryConfigurationStateService>();
        services.AddSingleton<IWinPeLanguageDiscoveryService, WinPeLanguageDiscoveryService>();
        services.AddSingleton<IConfigurationOverviewService, ConfigurationOverviewService>();
        services.AddSingleton<IWinPeEmbeddedAssetService, WinPeEmbeddedAssetService>();
        services.AddSingleton<IWinPeBuildService, WinPeBuildService>();
        services.AddSingleton<IWinPeWorkspacePreparationService, WinPeWorkspacePreparationService>();
        services.AddSingleton<IWinPeIsoMediaService, WinPeIsoMediaService>();
        services.AddSingleton<IWinPeUsbMediaService, WinPeUsbMediaService>();
        services.AddSingleton<IOperationProgressService, OperationProgressService>();
        services.AddSingleton<IAdkService, AdkService>();
        services.AddSingleton<IShellNavigationGuardService, ShellNavigationGuardService>();
        services.AddSingleton<IApplicationLocalizationService, ApplicationLocalizationService>();
        services.AddSingleton<IApplicationUpdateStateService, ApplicationUpdateStateService>();
        services.AddSingleton<IApplicationUpdateService, ApplicationUpdateService>();
        services.AddSingleton<IStartupReadinessService, StartupReadinessService>();
        services.AddSingleton<IGitHubRepositoryContributorService, GitHubRepositoryContributorService>();

        services.AddSingleton<IAppThemeService, AppThemeService>();
        services.AddSingleton<IAppNavigationService, AppNavigationService>();
        services.AddSingleton<INavigationStatusService, NavigationStatusService>();
        services.AddSingleton<IWindowsStartupService, WindowsStartupService>();
        services.AddSingleton<IApplicationLifetimeService, WinUiApplicationLifetimeService>();
        services.AddSingleton<IAppDispatcher, WinUiAppDispatcher>();
        services.AddSingleton<IDialogService, WinUiDialogService>();
        services.AddSingleton<IExternalProcessLauncher, WinUiExternalProcessLauncher>();
        services.AddSingleton<IFilePickerService, WinUiFilePickerService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<GeneralConfigurationViewModel>();
        services.AddTransient<UnattendConfigurationViewModel>();
        services.AddTransient<NetworkConfigurationViewModel>();
        services.AddTransient<AutopilotConfigurationViewModel>();
        services.AddTransient<CustomizationConfigurationViewModel>();
        services.AddTransient<StartMediaViewModel>();
        services.AddTransient<HomeLandingViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<GeneralSettingViewModel>();
        services.AddTransient<AdkPageViewModel>();
        services.AddTransient<AppUpdateSettingViewModel>();
        services.AddTransient<ProxySettingViewModel>();
        services.AddTransient<AboutUsSettingViewModel>();

        return services;
    }
}
