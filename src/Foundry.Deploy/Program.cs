// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Windows;
using System.Windows.Threading;
using Foundry.Deploy.Services.ApplicationShell;
using Foundry.Deploy.Services.Cache;
using Foundry.Deploy.Services.Catalog;
using Foundry.Deploy.Services.Configuration;
using Foundry.Deploy.DependencyInjection;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Deployment.Steps;
using Foundry.Deploy.Services.Download;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Operations;
using Foundry.Deploy.Services.Runtime;
using Foundry.Deploy.Services.System;
using Foundry.Deploy.Services.Theme;
using Foundry.Deploy.ViewModels;
using Foundry.Telemetry;
using Foundry.Utilities.Runtime;
using Foundry.Utilities.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Foundry.Deploy;

public static class Program
{
    private const string DisableFluentBackdropSwitch = "Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop";
    private static readonly TimeSpan RemoteDiagnosticsShutdownTimeout = TimeSpan.FromSeconds(2);

    [STAThread]
    public static int Main(string[] args)
    {
        string startupLogFilePath = FoundryDeployLogging.ResolveStartupLogFilePath();
        IHost? host = null;
        ITelemetryService? telemetryService = null;
        IRemoteDiagnosticsService? remoteDiagnosticsService = null;
        try
        {
            Log.Logger = FoundryDeployLogging.CreateLogger(startupLogFilePath);
        }
        catch (Exception ex)
        {
            startupLogFilePath = "<unavailable>";
            Log.Logger = VolumePathDiagnostics.WrapLogger(FoundryLogConfiguration.CreateDebugLogger(
                "Foundry.Deploy",
                DiagnosticSessionContext.CurrentSessionId,
                Serilog.Events.LogEventLevel.Debug,
                additionalSink: RemoteDiagnosticsSink.Instance));
            Log.ForContext(typeof(Program)).Error(ex, "File logging initialization failed. Falling back to debugger output.");
        }

        Serilog.ILogger programLogger = Log.ForContext(typeof(Program));
        RegisterGlobalExceptionHandlers();

        try
        {
            programLogger.Information(
                "Foundry.Deploy bootstrap started. Version={Version}, SessionId={SessionId}, LogFilePath={LogFilePath}",
                FoundryDeployApplicationInfo.Version,
                DiagnosticSessionContext.CurrentSessionId,
                startupLogFilePath);
            if (startupLogFilePath == "<unavailable>")
            {
                programLogger.Error("File logging is unavailable. Diagnostics are limited to debugger output.");
            }
            if (!RuntimeStartupGuard.CanRun())
            {
                programLogger.Error("Foundry.Deploy can only run in WinPE outside a DEBUG debugger session.");
                return 1;
            }

            ConfigureRuntimeCompatibility();

            host = BuildHost(args);
            telemetryService = host.Services.GetRequiredService<ITelemetryService>();
            remoteDiagnosticsService = host.Services.GetRequiredService<IRemoteDiagnosticsService>();
            InitializeRemoteDiagnostics(host.Services, remoteDiagnosticsService);

            App app = host.Services.GetRequiredService<App>();
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;
            app.InitializeComponent();

            MainWindow mainWindow = host.Services.GetRequiredService<MainWindow>();
            int exitCode = app.Run(mainWindow);
            programLogger.Debug("Flushing Foundry.Deploy telemetry events.");
            telemetryService.FlushAsync().GetAwaiter().GetResult();
            programLogger.Debug("Foundry.Deploy telemetry flush completed.");

            programLogger.Information("Foundry.Deploy exited with code {ExitCode}.", exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            programLogger.Fatal(ex, "Foundry.Deploy failed to start or terminated unexpectedly.");
            return 1;
        }
        finally
        {
            ShutdownRemoteDiagnostics(programLogger, remoteDiagnosticsService);
            host?.Dispose();
            Log.CloseAndFlush();
            FoundryDeployLogging.PersistCurrentLogs();
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

        return WinPeRuntimeDetector.IsWinPeRuntime();
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

        builder.Services.AddFoundryDeployApplicationServices();

        return builder.Build();
    }

    private static void InitializeRemoteDiagnostics(
        IServiceProvider services,
        IRemoteDiagnosticsService remoteDiagnosticsService)
    {
        TelemetrySettings telemetrySettings = services.GetRequiredService<TelemetrySettings>();
        TelemetryContext telemetryContext = services.GetRequiredService<TelemetryContext>();
        RemoteDiagnosticsLifecycle.Initialize(remoteDiagnosticsService, telemetrySettings, telemetryContext);
    }

    private static void ShutdownRemoteDiagnostics(
        Serilog.ILogger logger,
        IRemoteDiagnosticsService? remoteDiagnosticsService)
    {
        if (remoteDiagnosticsService is null)
        {
            return;
        }

        logger.Debug("Flushing Foundry.Deploy remote diagnostics.");
        using var cancellation = new CancellationTokenSource(RemoteDiagnosticsShutdownTimeout);
        try
        {
            RemoteDiagnosticsLifecycle.ShutdownAsync(remoteDiagnosticsService, cancellation.Token).GetAwaiter().GetResult();
            logger.Debug("Foundry.Deploy remote diagnostics flush completed.");
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Foundry.Deploy remote diagnostics flush timed out after {TimeoutSeconds} seconds.", RemoteDiagnosticsShutdownTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Foundry.Deploy remote diagnostics shutdown failed.");
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
        Application.Current?.Shutdown(1);
    }
}
