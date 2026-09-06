// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Foundry.Deploy.Services.Logging;

/// <summary>Projects volume paths into diagnostics without changing the retained execution locators.</summary>
internal static class VolumePathDiagnostics
{
    private static readonly Regex VolumeIdentifier = new(@"Volume\{[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Redact(string value) => VolumeIdentifier.Replace(value, "Volume{redacted}");

    /// <summary>Owns the destination logger and sanitizes events before its file, debug and remote sinks observe them.</summary>
    public static ILogger WrapLogger(ILogger destination) => new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .Enrich.FromLogContext()
        .WriteTo.Sink(new VolumePathSink(destination))
        .CreateLogger();

    private sealed class VolumePathSink(ILogger destination) : ILogEventSink, IDisposable
    {
        public void Emit(LogEvent logEvent)
        {
            string template = VolumeIdentifier.Replace(logEvent.MessageTemplate.Text, "Volume{{redacted}}");
            Exception? exception = logEvent.Exception;
            if (exception is not null && VolumeIdentifier.IsMatch(exception.ToString()))
                exception = new DiagnosticException(exception);
            destination.Write(new LogEvent(logEvent.Timestamp, logEvent.Level, exception,
                template == logEvent.MessageTemplate.Text ? logEvent.MessageTemplate : new MessageTemplateParser().Parse(template),
                logEvent.Properties.Select(property => new LogEventProperty(property.Key, Project(property.Value))),
                logEvent.TraceId ?? default, logEvent.SpanId ?? default));
        }

        public void Dispose() => (destination as IDisposable)?.Dispose();
    }

    private static LogEventPropertyValue Project(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string text } => new ScalarValue(Redact(text)),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(Project)),
        StructureValue structure => new StructureValue(structure.Properties.Select(property =>
            new LogEventProperty(property.Name, Project(property.Value))), structure.TypeTag),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(pair =>
            new KeyValuePair<ScalarValue, LogEventPropertyValue>((ScalarValue)Project(pair.Key), Project(pair.Value)))),
        _ => value
    };

    private sealed class DiagnosticException : Exception
    {
        private readonly string _display;
        public DiagnosticException(Exception original) : base(Redact(original.Message))
        {
            HResult = original.HResult;
            _display = Redact(original.ToString());
        }
        public override string ToString() => _display;
    }
}
