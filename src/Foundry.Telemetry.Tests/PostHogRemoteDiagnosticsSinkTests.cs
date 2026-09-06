// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Serilog.Events;
using Foundry.Telemetry;

namespace Foundry.Telemetry.Tests;

public sealed class PostHogRemoteDiagnosticsSinkTests
{
    [Fact]
    public async Task Emit_FiltersLevelsAndAllowsExplicitInformationBoundaries()
    {
        var exporter = new RecordingExporter();
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());

        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Debug, "debug"));
        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Information, "ordinary info"));
        service.Emit(RemoteDiagnosticsTestData.LogEvent(
            LogEventLevel.Information,
            "workflow boundary",
            properties: ("RemoteDiagnostic", true)));
        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Warning, "warning"));
        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "error"));
        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [LogEventLevel.Information, LogEventLevel.Warning, LogEventLevel.Error],
            exporter.Records.Select(static record => record.Level).ToArray());
    }

    [Fact]
    public async Task Emit_WhenDisabled_DoesNotCreateExporter()
    {
        int factoryCalls = 0;
        await using var service = new PostHogRemoteDiagnosticsSink(
            (_, _) =>
            {
                factoryCalls++;
                return new RecordingExporter();
            });

        service.Configure(RemoteDiagnosticsTestData.EnabledOptions() with { IsEnabled = false }, RemoteDiagnosticsTestData.Context());
        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "failed"));
        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Disable_StopsAcceptanceAndConfigureCanReenableExistingTransport()
    {
        var exporter = new RecordingExporter();
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());

        service.Disable();
        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "disabled"));
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "re-enabled"));
        await service.FlushAsync(TestContext.Current.CancellationToken);

        RemoteDiagnosticRecord record = Assert.Single(exporter.Records);
        Assert.Equal("re-enabled", record.Body);
    }

    [Fact]
    public async Task Disable_DropsBufferedRecordsButAllowsInFlightExportToFinish()
    {
        var exporter = new BlockingExporter();
        PostHogRemoteDiagnosticsSink service = CreateService(exporter);
        Exception? cleanupException = null;
        try
        {
            service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "in-flight"));
            Assert.True(exporter.Started.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "buffered"));

            service.Disable();
            exporter.Release.Set();
            service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "after-reenable"));
            await service.FlushAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["in-flight", "after-reenable"], exporter.Records.Select(static record => record.Body).ToArray());
        }
        finally
        {
            cleanupException = await Record.ExceptionAsync(
                () => ReleaseDrainAndDisposeAsync(exporter, service));
        }

        Assert.Null(cleanupException);
    }

    [Fact]
    public async Task Disable_ReenableResetsRateLimitAndExceptionDedupeState()
    {
        var exporter = new RecordingExporter();
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
        var firstException = new InvalidOperationException("failed");

        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "same failure", firstException));
        for (int index = 1; index < 5; index++)
        {
            service.Emit(RemoteDiagnosticsTestData.LogEvent(
                LogEventLevel.Error,
                "same failure",
                new InvalidOperationException("failed")));
        }

        Assert.True(await exporter.WaitForExportsAsync(
            5,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        service.Disable();
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "same failure", firstException));
        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, exporter.Records.Count);
    }

    [Fact]
    public async Task Emit_WhenExporterFails_DoesNotThrowAndContinuesDraining()
    {
        var exporter = new RecordingExporter { ThrowOnExport = true };
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());

        Exception? exception = Record.Exception(() =>
        {
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "first"));
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "second"));
        });
        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Null(exception);
        Assert.Equal(2, exporter.ExportAttempts);
    }

    [Fact]
    public async Task Emit_DropsDuplicateExceptionInstance()
    {
        var exporter = new RecordingExporter();
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
        var sharedException = new InvalidOperationException("failed");

        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "failed", sharedException));
        service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "failed again", sharedException));
        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Single(exporter.Records);
    }

    [Fact]
    public async Task Emit_SameExceptionInDifferentOperations_IsRetained()
    {
        var exporter = new RecordingExporter();
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
        var sharedException = new InvalidOperationException("failed");

        service.Emit(RemoteDiagnosticsTestData.LogEvent(
            LogEventLevel.Error,
            "first failed",
            sharedException,
            ("OperationId", "operation-1")));
        service.Emit(RemoteDiagnosticsTestData.LogEvent(
            LogEventLevel.Error,
            "second failed",
            sharedException,
            ("OperationId", "operation-2")));
        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, exporter.Records.Count);
    }

    [Fact]
    public async Task Emit_RateLimitsRepeatedFingerprint()
    {
        var exporter = new RecordingExporter();
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());

        for (int index = 0; index < 8; index++)
        {
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Warning, "same warning"));
        }

        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, exporter.Records.Count);
    }

    [Fact]
    public async Task Emit_RateLimitsRepeatedFingerprintAcrossOperations()
    {
        var exporter = new RecordingExporter();
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());

        for (int index = 0; index < 8; index++)
        {
            service.Emit(RemoteDiagnosticsTestData.LogEvent(
                LogEventLevel.Warning,
                "same warning",
                properties: ("OperationId", $"operation-{index}")));
        }

        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, exporter.Records.Count);
    }

    [Fact]
    public async Task Emit_WhenQueueIsFull_DropsWithoutBlocking()
    {
        var exporter = new BlockingExporter();
        PostHogRemoteDiagnosticsSink service = CreateService(exporter, queueCapacity: 1);
        Exception? cleanupException = null;
        try
        {
            service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "first"));
            Assert.True(exporter.Started.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "second"));
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "third"));

            Assert.Equal(1, service.DroppedRecordCount);
            exporter.Release.Set();
            await service.FlushAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            cleanupException = await Record.ExceptionAsync(
                () => ReleaseDrainAndDisposeAsync(exporter, service));
        }

        Assert.Null(cleanupException);
    }

    [Fact]
    public async Task FlushAsync_WhenExporterIsBlocked_ObservesCancellation()
    {
        var exporter = new BlockingExporter();
        PostHogRemoteDiagnosticsSink service = CreateService(exporter);
        Exception? cleanupException = null;
        try
        {
            service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());
            service.Emit(RemoteDiagnosticsTestData.LogEvent(LogEventLevel.Error, "failed"));
            Assert.True(exporter.Started.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FlushAsync(cancellation.Token));
        }
        finally
        {
            cleanupException = await Record.ExceptionAsync(
                () => ReleaseDrainAndDisposeAsync(exporter, service));
        }

        Assert.Null(cleanupException);
    }

    [Fact]
    public async Task Emit_InternalExporterEvent_IsExcluded()
    {
        var exporter = new RecordingExporter();
        await using var service = CreateService(exporter);
        service.Configure(RemoteDiagnosticsTestData.EnabledOptions(), RemoteDiagnosticsTestData.Context());

        service.Emit(RemoteDiagnosticsTestData.LogEvent(
            LogEventLevel.Error,
            "exporter failure",
            properties: ("RemoteDiagnosticsInternal", true)));
        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Empty(exporter.Records);
    }

    [Fact]
    public void CreateLogEvent_DoesNotDuplicateResourceAttributes()
    {
        var record = new RemoteDiagnosticRecord(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            "Deployment failed for {Path}",
            new Dictionary<string, object>
            {
                ["service.name"] = "foundry.deploy",
                ["service.version"] = "1.2.3",
                ["service.release"] = "foundry.deploy@1.2.3",
                ["runtime.name"] = "winpe",
                ["runtime.architecture"] = "x64",
                ["operation.id"] = "operation-1",
                ["failure.operation"] = "windows_optional_features.validate"
            },
            new RemoteDiagnosticException("System.InvalidOperationException", "redacted", "at Foundry.Run()", []));

        LogEvent logEvent = PostHogDiagnosticsExporter.CreateLogEvent(record);

        Assert.Null(logEvent.Exception);
        Assert.Equal("Deployment failed for {Path}", logEvent.RenderMessage());
        Assert.DoesNotContain("DiagnosticBody", logEvent.Properties.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("service.name", logEvent.Properties.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("service.version", logEvent.Properties.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("service.release", logEvent.Properties.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("runtime.name", logEvent.Properties.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("runtime.architecture", logEvent.Properties.Keys, StringComparer.Ordinal);
        Assert.Equal("operation-1", Assert.IsType<ScalarValue>(logEvent.Properties["operation.id"]).Value);
        Assert.Equal(
            "windows_optional_features.validate",
            Assert.IsType<ScalarValue>(logEvent.Properties["failure.operation"]).Value);
        Assert.Equal("redacted", Assert.IsType<ScalarValue>(logEvent.Properties["exception.message"]).Value);
    }

    [Fact]
    public void CreateLogEvent_WhenExceptionMessageMatchesBody_DoesNotDuplicateMessage()
    {
        LogEvent source = RemoteDiagnosticsTestData.LogEvent(
            LogEventLevel.Error,
            "Deployment failed",
            new InvalidOperationException("private detail"));
        RemoteDiagnosticRecord record = RemoteDiagnosticPropertyPolicy.CreateSanitizedRecord(
            source,
            RemoteDiagnosticsTestData.Context());

        LogEvent logEvent = PostHogDiagnosticsExporter.CreateLogEvent(record);

        Assert.Equal("Deployment failed", logEvent.RenderMessage());
        Assert.DoesNotContain("exception.message", logEvent.Properties.Keys, StringComparer.Ordinal);
        Assert.Contains("exception.type", logEvent.Properties.Keys, StringComparer.Ordinal);
    }

    private static PostHogRemoteDiagnosticsSink CreateService(
        IRemoteDiagnosticsExporter exporter,
        int queueCapacity = 32) =>
        new((_, _) => exporter, queueCapacity);

    private static async Task ReleaseDrainAndDisposeAsync(
        BlockingExporter exporter,
        PostHogRemoteDiagnosticsSink service)
    {
        exporter.Release.Set();
        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.FlushAsync(cleanupCancellation.Token);
        await service.DisposeAsync();
    }

    private class RecordingExporter : IRemoteDiagnosticsExporter
    {
        private readonly SemaphoreSlim exportSignal = new(0);

        public List<RemoteDiagnosticRecord> Records { get; } = [];

        public int ExportAttempts { get; private set; }

        public bool ThrowOnExport { get; init; }

        public virtual ValueTask ExportAsync(RemoteDiagnosticRecord record, CancellationToken cancellationToken)
        {
            ExportAttempts++;
            if (ThrowOnExport)
            {
                throw new InvalidOperationException("export failed");
            }

            Records.Add(record);
            exportSignal.Release();
            return ValueTask.CompletedTask;
        }

        public async Task<bool> WaitForExportsAsync(
            int count,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            try
            {
                for (int index = 0; index < count; index++)
                {
                    await exportSignal.WaitAsync(timeoutCancellation.Token);
                }

                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        public virtual Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual ValueTask DisposeAsync()
        {
            exportSignal.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingExporter : RecordingExporter
    {
        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public override ValueTask ExportAsync(RemoteDiagnosticRecord record, CancellationToken cancellationToken)
        {
            Started.Set();
            Release.Wait(cancellationToken);
            return base.ExportAsync(record, cancellationToken);
        }

        public override ValueTask DisposeAsync()
        {
            Started.Dispose();
            Release.Dispose();
            return base.DisposeAsync();
        }
    }
}
