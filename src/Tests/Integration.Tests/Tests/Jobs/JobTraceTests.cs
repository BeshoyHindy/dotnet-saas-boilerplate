using System.Collections.Concurrent;
using System.Diagnostics;
using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Jobs.Services;
using Hangfire;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Jobs;

/// <summary>What a job's span looked like from inside the job.</summary>
public sealed record TraceObservation(ActivityTraceId TraceId, ActivitySpanId ParentSpanId);

/// <summary>Records the trace the job ran in. Tenant-less so the test needs no tenant fixture.</summary>
[SystemJob]
[AutomaticRetry(Attempts = 0)]
public static class TraceProbeJob
{
    public static ConcurrentDictionary<Guid, TraceObservation?> Observations { get; } = new();

    public static Task RunAsync(Guid marker, CancellationToken cancellationToken)
    {
        var current = Activity.Current;
        Observations[marker] = current is null ? null : new TraceObservation(current.TraceId, current.ParentSpanId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A job enqueued inside a trace — a request's, or another job's — runs as a child span of it, so
/// the request and the job it caused are one trace (<c>HangfireTelemetryFilter</c>). A job enqueued
/// with no trace in scope, as a recurring trigger is, still starts a root span.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class JobTraceTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);

    private readonly AppWebApplicationFactory _factory;

    public JobTraceTests(AppWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task A_Job_Enqueued_Inside_A_Trace_Should_Run_As_A_Child_Of_The_Enqueuing_Span()
    {
        using var listener = ListenToJobSpans();
        var marker = Guid.CreateVersion7();

        ActivityTraceId enqueuingTrace;
        ActivitySpanId enqueuingSpan;
        using (var request = new Activity("enqueuing-request").Start())
        {
            enqueuingTrace = request.TraceId;
            enqueuingSpan = request.SpanId;
            Enqueue(marker);
        }

        var observed = await WaitForAsync(marker);

        observed.ShouldNotBeNull("the job must run inside its own span");
        observed.TraceId.ShouldBe(enqueuingTrace, "the job must join the trace that enqueued it");
        observed.ParentSpanId.ShouldBe(enqueuingSpan, "the job span's parent is the span that enqueued it");
    }

    [Fact]
    public async Task A_Job_Enqueued_With_No_Trace_Should_Start_A_Root_Span()
    {
        using var listener = ListenToJobSpans();
        var marker = Guid.CreateVersion7();

        Activity.Current = null;
        Enqueue(marker);

        var observed = await WaitForAsync(marker);

        observed.ShouldNotBeNull("the job must run inside its own span");
        observed.ParentSpanId.ShouldBe(default, "with nothing to link to, the job span is a root");
    }

    /// <summary>
    /// OpenTelemetry is off in the test host, so nothing samples the job's source unless the test
    /// does — without a listener <c>StartActivity</c> returns null and there is no span to inspect.
    /// </summary>
    private static ActivityListener ListenToJobSpans()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Boilerplate.Hangfire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private void Enqueue(Guid marker)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IJobService>()
            .Enqueue(() => TraceProbeJob.RunAsync(marker, CancellationToken.None));
    }

    private static async Task<TraceObservation?> WaitForAsync(Guid marker)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TraceProbeJob.Observations.TryGetValue(marker, out var observation))
            {
                return observation;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"The trace probe job did not run within {JobTimeout}.");
    }
}
