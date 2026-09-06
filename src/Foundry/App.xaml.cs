// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.DependencyInjection;
using Foundry.Core.Services.Application;
using Foundry.Core.Services.Configuration;
using Foundry.Services.Configuration;
using Foundry.Services.Appearance;
using Foundry.Services.Localization;
using Foundry.Services.Networking;
using Foundry.Services.Settings;
using Foundry.Services.Shell;
using Foundry.Services.Startup;
using Foundry.Telemetry;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Foundry
{
    /// <summary>
    /// Owns the WinUI application lifetime and exposes the host services used by XAML pages.
    /// </summary>
    public partial class App : Application
    {
        private static readonly ILogger AppLogger = Log.ForContext<App>();
        private static readonly TimeSpan RemoteDiagnosticsShutdownTimeout = TimeSpan.FromSeconds(2);
        private bool isShuttingDown;

        /// <summary>
        /// Gets the active Foundry application instance.
        /// </summary>
        public new static App Current => (App)Application.Current;

        /// <summary>
        /// Gets the main window once the application has launched.
        /// </summary>
        public static Window MainWindow = Window.Current;

        /// <summary>
        /// Gets the dependency injection host for the application.
        /// </summary>
        public IHost Host { get; }

        /// <summary>
        /// Gets the service provider rooted in <see cref="Host"/>.
        /// </summary>
        public IServiceProvider Services => Host.Services;

        /// <summary>
        /// Gets the native WinUI shell navigation service.
        /// </summary>
        public IAppNavigationService NavigationService => GetService<IAppNavigationService>();

        /// <summary>
        /// Gets the theme service used to apply runtime theme changes.
        /// </summary>
        public IAppThemeService ThemeService => GetService<IAppThemeService>();

        /// <summary>
        /// Resolves a required service from the application host.
        /// </summary>
        /// <typeparam name="T">The service contract type.</typeparam>
        /// <returns>The registered service instance.</returns>
        /// <exception cref="ArgumentException">Thrown when the requested service has not been registered.</exception>
        public static T GetService<T>() where T : class
        {
            if (Current.Services.GetService(typeof(T)) is not T service)
            {
                throw new ArgumentException($"{typeof(T)} needs to be registered in {nameof(ServiceCollectionExtensions)}.");
            }

            return service;
        }

        /// <summary>
        /// Creates the application host, initializes settings and localization, and loads the XAML application.
        /// </summary>
        public App()
        {
            Host = FoundryHost.Create();
            _ = Host.Services.GetRequiredService<IApplicationProxyService>();
            IAppSettingsService appSettingsService = Host.Services.GetRequiredService<IAppSettingsService>();
            SetDeveloperModeEnabled(appSettingsService.Current.Diagnostics.DeveloperMode);
            Host.Services.GetRequiredService<IApplicationLocalizationService>().InitializeAsync().GetAwaiter().GetResult();
            InitializeRemoteDiagnostics(Host.Services.GetRequiredService<TelemetrySettings>());
            RegisterWinUiExceptionHandler();

            AppLogger.Information("Foundry WinUI host initialized.");
            this.InitializeComponent();
        }

        /// <summary>
        /// Creates and activates the main window, then starts application readiness checks.
        /// </summary>
        /// <param name="args">Launch activation arguments provided by WinUI.</param>
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                _ = GetService<IFoundryConfigurationStateService>();

                MainWindow mainWindow = GetService<MainWindow>();
                MainWindow = mainWindow;
                mainWindow.Closed += OnMainWindowClosed;

                mainWindow.Title = mainWindow.AppWindow.Title = FoundryApplicationInfo.AppNameAndVersion;
                mainWindow.AppWindow.SetIcon("Assets/AppIcon.ico");

                ThemeService.Initialize(mainWindow, mainWindow.RootElement);

                mainWindow.Activate();

                await InitializeAppAsync();
            }
            catch (UnsupportedConfigurationVersionException ex)
            {
                AppLogger.Error(
                    ex,
                    "Foundry startup blocked by an unsupported configuration schema. StatePath={StatePath}",
                    Constants.FoundryConfigurationStatePath);
                await ShowUnsupportedConfigurationVersionAsync(ex);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Foundry WinUI launch failed.");
                throw;
            }
        }

        private async Task ShowUnsupportedConfigurationVersionAsync(UnsupportedConfigurationVersionException exception)
        {
            var blockingWindow = new Window
            {
                Content = new Grid(),
                Title = FoundryApplicationInfo.AppNameAndVersion
            };
            MainWindow = blockingWindow;
            blockingWindow.Closed += OnMainWindowClosed;
            blockingWindow.Activate();

            string message = $"{exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"Update Foundry before opening this file. Foundry left it unchanged:{Environment.NewLine}" +
                Constants.FoundryConfigurationStatePath;
            IApplicationLocalizationService localizationService = GetService<IApplicationLocalizationService>();
            await GetService<IDialogService>().ShowMessageAsync(new DialogRequest(
                FoundryApplicationInfo.AppNameAndVersion,
                message,
                localizationService.GetString("Common.Close")));
            blockingWindow.Close();
        }

        private static async Task InitializeAppAsync()
        {
            await GetService<IStartupReadinessService>().InitializeAsync();
            await TrackDailyActiveAsync();
            AppLogger.Information("Foundry WinUI startup completed.");
        }

        private static async Task TrackDailyActiveAsync()
        {
            IAppSettingsService settingsService = GetService<IAppSettingsService>();
            if (!settingsService.Current.Telemetry.IsEnabled)
            {
                return;
            }

            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            if (!TelemetryDailyActivityGate.ShouldTrack(today, settingsService.Current.Telemetry.LastDailyActiveDate))
            {
                return;
            }

            AppLogger.Debug("Tracking Foundry daily-active telemetry event.");
            ProxyAppSettings proxy = settingsService.Current.Proxy;
            await GetService<ITelemetryService>().TrackAsync(TelemetryEvents.AppDailyActive, new Dictionary<string, object?>
            {
                ["proxy_method"] = proxy.Method.ToString().ToLowerInvariant(),
                ["proxy_authentication_mode"] = proxy.Method == ProxyMethod.Manual
                    ? proxy.AuthenticationMode.ToString().ToLowerInvariant()
                    : "not_applicable"
            });
            settingsService.Current.Telemetry.LastDailyActiveDate = TelemetryDailyActivityGate.FormatDate(today);
            settingsService.Save();
            AppLogger.Debug("Foundry daily-active telemetry event queued.");
        }

        private void RegisterWinUiExceptionHandler()
        {
            UnhandledException += OnWinUiUnhandledException;
        }

        private static void OnWinUiUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            AppLogger.Fatal(e.Exception, "Unhandled WinUI exception.");
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            if (isShuttingDown)
            {
                return;
            }

            isShuttingDown = true;
            AppLogger.Information("Foundry WinUI shutdown started.");
            AppLogger.Debug("Flushing Foundry telemetry events.");
            GetService<ITelemetryService>().FlushAsync().GetAwaiter().GetResult();
            AppLogger.Debug("Foundry telemetry flush completed.");
            ShutdownRemoteDiagnostics();
            Host.Dispose();
            AppLogger.Information("Foundry WinUI shutdown completed.");
            Log.CloseAndFlush();
        }

        private void InitializeRemoteDiagnostics(TelemetrySettings telemetrySettings)
        {
            RemoteDiagnosticsLifecycle.Initialize(
                Host.Services.GetRequiredService<IRemoteDiagnosticsService>(),
                telemetrySettings,
                Host.Services.GetRequiredService<TelemetryContext>());
        }

        private void ShutdownRemoteDiagnostics()
        {
            AppLogger.Debug("Flushing Foundry remote diagnostics.");
            using var cancellation = new CancellationTokenSource(RemoteDiagnosticsShutdownTimeout);
            try
            {
                RemoteDiagnosticsLifecycle.ShutdownAsync(
                    GetService<IRemoteDiagnosticsService>(),
                    cancellation.Token).GetAwaiter().GetResult();
                AppLogger.Debug("Foundry remote diagnostics flush completed.");
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warning(
                    "Foundry remote diagnostics flush timed out after {TimeoutSeconds} seconds.",
                    RemoteDiagnosticsShutdownTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "Foundry remote diagnostics shutdown failed.");
            }
        }

    }
}
