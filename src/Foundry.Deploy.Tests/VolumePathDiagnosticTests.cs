// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Deployment.Steps;
using Foundry.Deploy.Services.Logging;
using Serilog;

namespace Foundry.Deploy.Tests;

[Collection(nameof(SerilogCollection))]
public sealed class VolumePathDiagnosticTests
{
    internal const string WindowsRoot = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";
    internal const string SystemRoot = @"\\?\Volume{66666666-7777-8888-9999-aaaaaaaaaaaa}\";
    internal const string RecoveryRoot = @"\\?\Volume{bbbbbbbb-cccc-dddd-eeee-ffffffffffff}\";
    private static readonly string[] Identifiers = ["11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-aaaaaaaaaaaa", "bbbbbbbb-cccc-dddd-eeee-ffffffffffff"];

    [Fact]
    public async Task ApplicationLogs_RedactVolumeIdentifiersFromMessagesPropertiesAndFailures()
    {
        string directory = Directory.CreateTempSubdirectory("foundry-volume-log-").FullName;
        string path = Path.Combine(directory, "FoundryDeploy.log");
        try
        {
            Log.Logger = FoundryDeployLogging.CreateLogger(path);
            var service = new DeploymentLogService();
            DeploymentLogSession session = service.Initialize(directory);
            await service.AppendAsync(session, DeploymentLogLevel.Info,
                $"Prepared system={SystemRoot}, windows={WindowsRoot}, recovery={RecoveryRoot}.", TestContext.Current.CancellationToken);
            Log.ForContext("WorkingDirectory", WindowsRoot + "Foundry").Information(
                "Process {FileName} {Arguments}", WindowsRoot + "Windows\\System32\\bcdboot.exe", $"{WindowsRoot} /s {SystemRoot}");
            Log.Error(new IOException("Failed at " + RecoveryRoot, new IOException("Nested " + SystemRoot)),
                "Failure with paths {@Paths}", new { Windows = WindowsRoot, Items = new[] { SystemRoot, RecoveryRoot } });
            Log.Information($"Interpolated root {WindowsRoot}");
            using (Serilog.Context.LogContext.PushProperty("VolumeContext", RecoveryRoot))
                Log.Information("Context {VolumeContext}");
            Log.CloseAndFlush();
            string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            AssertNoIdentifiers(text);
            Assert.Contains("Volume{redacted}", text, StringComparison.Ordinal);
            Assert.Contains("IOException", text, StringComparison.Ordinal);
        }
        finally
        {
            Log.CloseAndFlush();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DiagnosticSink_PreservesStructuredTypesAndOriginalEvent()
    {
        var sink = new RecordingSink();
        using var destination = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var logger = (IDisposable)VolumePathDiagnostics.WrapLogger(destination);
        var source = new Serilog.Events.LogEvent(DateTimeOffset.UtcNow, Serilog.Events.LogEventLevel.Error,
            new IOException("Failure " + WindowsRoot), new Serilog.Parsing.MessageTemplateParser().Parse("Paths {@Paths} {Count} {Flag}"),
            [new("Paths", new Serilog.Events.StructureValue([new("Root", new Serilog.Events.ScalarValue(RecoveryRoot))], "Partition")),
             new("Count", new Serilog.Events.ScalarValue(7)), new("Flag", new Serilog.Events.ScalarValue(true))]);
        ((Serilog.ILogger)logger).Write(source);
        Serilog.Events.LogEvent projected = Assert.Single(sink.Events);
        Assert.Equal(7, Assert.IsType<Serilog.Events.ScalarValue>(projected.Properties["Count"]).Value);
        Assert.Equal(true, Assert.IsType<Serilog.Events.ScalarValue>(projected.Properties["Flag"]).Value);
        Serilog.Events.StructureValue paths = Assert.IsType<Serilog.Events.StructureValue>(projected.Properties["Paths"]);
        Assert.Equal("Partition", paths.TypeTag);
        Assert.Equal("Root", Assert.Single(paths.Properties).Name);
        AssertNoIdentifiers(projected.RenderMessage() + projected.Exception);
        Assert.Contains(RecoveryRoot, source.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains(WindowsRoot, source.Exception!.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = [];
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public async Task DiagnosticStateAndSummary_RedactCopiesWithoutChangingLivePaths()
    {
        string directory = Directory.CreateTempSubdirectory("foundry-volume-state-").FullName;
        try
        {
            var state = new DeploymentRuntimeState
            {
                TargetWindowsPartitionRoot = WindowsRoot,
                TargetSystemPartitionRoot = SystemRoot,
                TargetRecoveryPartitionRoot = RecoveryRoot,
                TargetFoundryRoot = WindowsRoot + "Foundry",
                DownloadedOperatingSystemPath = WindowsRoot + "Foundry\\install.wim",
                PreOobeScriptPaths = [WindowsRoot + "script.ps1"],
                TargetComputerName = "UNCHANGED-OB03"
            };
            var service = new DeploymentLogService();
            DeploymentLogSession session = service.Initialize(directory);
            await service.SaveStateAsync(session, state, TestContext.Current.CancellationToken);
            string summary = Path.Combine(directory, "summary.json");
            await FinalizeDeploymentAndWriteLogsStep.WriteDeploymentSummaryAsync(summary, state, TestContext.Current.CancellationToken);
            foreach (string artifact in new[] { session.StateFilePath, summary })
            {
                string json = await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken);
                AssertNoIdentifiers(json);
                using JsonDocument parsed = JsonDocument.Parse(json);
                Assert.Contains("UNCHANGED-OB03", json, StringComparison.Ordinal);
            }
            Assert.Equal(WindowsRoot, state.TargetWindowsPartitionRoot);
            Assert.Equal(SystemRoot, state.TargetSystemPartitionRoot);
            Assert.Equal(RecoveryRoot, state.TargetRecoveryPartitionRoot);
            Assert.Equal(WindowsRoot + "script.ps1", Assert.Single(state.PreOobeScriptPaths));
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static void AssertNoIdentifiers(string text)
    {
        foreach (string identifier in Identifiers) Assert.DoesNotContain(identifier, text, StringComparison.OrdinalIgnoreCase);
    }
}
