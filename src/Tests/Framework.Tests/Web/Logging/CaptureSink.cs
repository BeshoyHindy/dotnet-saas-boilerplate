using System.Collections.Concurrent;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Framework.Tests.Web.Logging;

/// <summary>
/// A Serilog sink the <c>Serilog</c> config section can name (<c>"Using": ["Boilerplate.Framework.Tests"]</c>,
/// <c>"WriteTo": [{ "Name": "Capture" }]</c>), so a test host wired by <c>AddHeroLogging</c> — which
/// builds its logger from configuration only — writes into memory the test can read.
/// Shared across tests: each test filters by a marker of its own (a unique path or message).
/// </summary>
public sealed class CaptureSink : ILogEventSink
{
    public static CaptureSink Instance { get; } = new();

    public ConcurrentQueue<LogEvent> Events { get; } = new();

    public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
}

public static class CaptureSinkExtensions
{
    public static LoggerConfiguration Capture(this LoggerSinkConfiguration sinkConfiguration)
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);
        return sinkConfiguration.Sink(CaptureSink.Instance);
    }
}
