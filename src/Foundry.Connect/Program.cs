// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Windows;
using System.Windows.Threading;
using Foundry.Connect.DependencyInjection;
using Foundry.Connect.Models;
using Foundry.Connect.Models.Configuration;
using Foundry.Connect.Services.Configuration;
using Foundry.Connect.Services.Logging;
using Foundry.Connect.Services.Runtime;
using Foundry.Core.Services.Configuration;
using Foundry.Telemetry;
using Foundry.Utilities.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Foundry.Connect;

/// <summary>
/// Provides the WPF entry point for Foundry.Connect in WinPE.
/// </summary>
public static class Program
{
    private const string DisableFluentBackdropSwitch = "Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop";
    private static readonly TimeSpan RemoteDiagnosticsShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Configures logging, validates runtime constraints, builds the host, and runs the WPF shell.
    /// </summary>
    /// <param name="args">Command-line arguments passed to Foundry.Connect.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        string startupLogFilePath = FoundryConnectLogging.ResolveStartupLogFilePath();
        IHost? host = null;
        ITelemetryService? telemetryService = null;
        IRemoteDiagnosticsService? remoteDiagnosticsService = null;
        try
        {
            Log.Logger = FoundryConnectLogging.CreateLogger(startupLogFilePath);
        }
        catch (Exception ex)
        {
            startupLogFilePath = "<unavailable>";
            Log.Logger = FoundryLogConfiguration.CreateDebugLogger(
                "Foundry.Connect",
                DiagnosticSessionContext.CurrentSessionId,
                Serilog.Events.LogEventLevel.Debug,
                additionalSink: RemoteDiagnosticsSink.Instance);
            Log.ForContext(typeof(Program)).Error(ex, "File logging initialization failed. Falling back to debugger output.");
        }

        Serilog.ILogger programLogger = Log.ForContext(typeof(Program));
        RegisterGlobalExceptionHandlers();

        try
        {
            programLogger.Information(
                "Foundry.Connect bootstrap started. Version={Version}, SessionId={SessionId}, LogFilePath={LogFilePath}",
                FoundryConnectApplicationInfo.Version,
                DiagnosticSessionContext.CurrentSessionId,
                startupLogFilePath);
            if (startupLogFilePath == "<unavailable>")
            {
                programLogger.Error("File logging is unavailable. Diagnostics are limited to debugger output.");
            }
            if (!RuntimeStartupGuard.CanRun())
            {
                programLogger.Error("Foundry.Connect can only run in WinPE outside a DEBUG debugger session.");
                return (int)FoundryConnectExitCode.StartupFailure;
            }

            ConfigureRuntimeCompatibility();
            programLogger.Information("Runtime compatibility configuration completed.");

            host = BuildHost(args);
            programLogger.Information("Host built successfully.");
            telemetryService = host.Services.GetRequiredService<ITelemetryService>();
            remoteDiagnosticsService = host.Services.GetRequiredService<IRemoteDiagnosticsService>();
            InitializeRemoteDiagnostics(host.Services, remoteDiagnosticsService);

            App app = host.Services.GetRequiredService<App>();
            programLogger.Debug("Resolved App instance.");
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;
            app.InitializeComponent();
            programLogger.Debug("App.InitializeComponent completed.");

            MainWindow mainWindow = host.Services.GetRequiredService<MainWindow>();
            programLogger.Debug("Resolved MainWindow instance.");
            programLogger.Information("Entering WPF run loop.");
            int exitCode = app.Run(mainWindow);
            programLogger.Debug("Flushing Foundry.Connect telemetry events.");
            telemetryService.FlushAsync().GetAwaiter().GetResult();
            programLogger.Debug("Foundry.Connect telemetry flush completed.");

            programLogger.Information(
                "Foundry.Connect exited. Outcome={Outcome}, ExitCode={ExitCode}",
                exitCode == (int)FoundryConnectExitCode.Success ? "Succeeded" : "Stopped",
                exitCode);
            return exitCode;
        }
        catch (FoundryConnectConfigurationException ex)
        {
            programLogger.Fatal(
                ex,
                "Foundry.Connect configuration could not be loaded. Outcome={Outcome}, ExitCode={ExitCode}",
                "ConfigurationFailure",
                (int)FoundryConnectExitCode.ConfigurationFailure);
            if (ex.InnerException is UnsupportedConfigurationVersionException)
            {
                try
                {
                    _ = MessageBox.Show(
                        $"{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                        "Update Foundry before using this configuration. The configuration file was not changed.",
                        $"{FoundryConnectApplicationInfo.AppName} configuration update required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        MessageBoxResult.OK);
                }
                catch (Exception dialogException)
                {
                    programLogger.Error(dialogException, "The unsupported configuration version dialog could not be displayed.");
                }
            }

            return (int)FoundryConnectExitCode.ConfigurationFailure;
        }
        catch (Exception ex)
        {
            programLogger.Fatal(
                ex,
                "Foundry.Connect failed to start or terminated unexpectedly. Outcome={Outcome}, ExitCode={ExitCode}",
                "StartupFailure",
                (int)FoundryConnectExitCode.StartupFailure);
            return (int)FoundryConnectExitCode.StartupFailure;
        }
        finally
        {
            ShutdownRemoteDiagnostics(programLogger, remoteDiagnosticsService);
            host?.Dispose();
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureRuntimeCompatibility()
    {
        if (!ShouldDisableFluentBackdrop())
        {
            return;
        }

        AppContext.SetSwitch(DisableFluentBackdropSwitch, true);
        Log.Information("Enabled '{SwitchName}'.", DisableFluentBackdropSwitch);
    }

    private static bool ShouldDisableFluentBackdrop()
    {
        string? overrideValue = Environment.GetEnvironmentVariable("FOUNDRY_DISABLE_FLUENT_BACKDROP");
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return IsTruthy(overrideValue);
        }

        // WinPE does not provide the full desktop composition stack required by WPF fluent backdrop effects.
        return ConnectWorkspacePaths.IsWinPeRuntime();
    }

    private static bool IsTruthy(string value)
    {
        return value.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            "on" => true,
            "ON" => true,
            _ => false
        };
    }

    private static IHost BuildHost(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: false);
        builder.Services.AddFoundryConnectApplicationServices(args);

        return builder.Build();
    }

    private static void InitializeRemoteDiagnostics(
        IServiceProvider services,
        IRemoteDiagnosticsService remoteDiagnosticsService)
    {
        FoundryConnectConfiguration configuration = services.GetRequiredService<FoundryConnectConfiguration>();
        TelemetryContext telemetryContext = services.GetRequiredService<TelemetryContext>();
        RemoteDiagnosticsLifecycle.Initialize(remoteDiagnosticsService, configuration.Telemetry, telemetryContext);
    }

    private static void ShutdownRemoteDiagnostics(
        Serilog.ILogger logger,
        IRemoteDiagnosticsService? remoteDiagnosticsService)
    {
        if (remoteDiagnosticsService is null)
        {
            return;
        }

        logger.Debug("Flushing Foundry.Connect remote diagnostics.");
        using var cancellation = new CancellationTokenSource(RemoteDiagnosticsShutdownTimeout);
        try
        {
            RemoteDiagnosticsLifecycle.ShutdownAsync(remoteDiagnosticsService, cancellation.Token).GetAwaiter().GetResult();
            logger.Debug("Foundry.Connect remote diagnostics flush completed.");
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Foundry.Connect remote diagnostics flush timed out after {TimeoutSeconds} seconds.", RemoteDiagnosticsShutdownTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Foundry.Connect remote diagnostics shutdown failed.");
        }
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Serilog.ILogger logger = Log.ForContext(typeof(Program));
            if (args.ExceptionObject is Exception exception)
            {
                logger.Fatal(exception, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", args.IsTerminating);
                if (args.IsTerminating)
                {
                    Log.CloseAndFlush();
                }

                return;
            }

            logger.Fatal("Unhandled AppDomain exception. IsTerminating={IsTerminating}, ExceptionObject={ExceptionObject}",
                args.IsTerminating,
                args.ExceptionObject);
            if (args.IsTerminating)
            {
                Log.CloseAndFlush();
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.ForContext(typeof(Program)).Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Log.ForContext(typeof(Program)).Fatal(args.Exception, "Unhandled WPF dispatcher exception.");
        args.Handled = true;

        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Shutdown((int)FoundryConnectExitCode.StartupFailure);
    }
}
