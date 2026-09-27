using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using System.Diagnostics;

namespace Boilerplate.BuildingBlocks.Jobs;

/// <summary>
/// Traces a Hangfire job back to whatever enqueued it.
///
/// On enqueue (<see cref="IClientFilter"/>) the current trace context — the request's, or the
/// enclosing job's — is stored on the job as W3C <c>traceparent</c>/<c>tracestate</c> parameters. On
/// execution (<see cref="IServerFilter"/>) the job's span starts as a child of that context, so a
/// request and the job it caused are one trace. A job enqueued with no trace in scope (a recurring
/// trigger) has no parameter and starts a root span, as before.
/// </summary>
public sealed class HangfireTelemetryFilter : JobFilterAttribute, IClientFilter, IServerFilter
{
    /// <summary>Job parameter carrying the enqueuer's W3C <c>traceparent</c>.</summary>
    internal const string TraceParentParameter = "traceparent";

    /// <summary>Job parameter carrying the enqueuer's W3C <c>tracestate</c>, when it has one.</summary>
    internal const string TraceStateParameter = "tracestate";

    private const string ActivityKey = "__boilerplate_activity";
    private static readonly ActivitySource ActivitySource = new("Boilerplate.Hangfire");

    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var current = Activity.Current;
        if (current is null || current.IdFormat != ActivityIdFormat.W3C || current.Id is null)
        {
            return;
        }

        context.SetJobParameter(TraceParentParameter, current.Id);
        if (!string.IsNullOrEmpty(current.TraceStateString))
        {
            context.SetJobParameter(TraceStateParameter, current.TraceStateString);
        }
    }

    public void OnCreated(CreatedContext context)
    {
        // Nothing to do once the job exists; the trace context was captured in OnCreating.
    }

    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var job = context.BackgroundJob?.Job;
        string name = job is null
            ? "Hangfire.Job"
            : $"{job.Type.Name}.{job.Method.Name}";

        var activity = ActivitySource.StartActivity(name, ActivityKind.Internal, ReadParent(context));
        if (activity is null)
        {
            return;
        }

        activity.SetTag("hangfire.job_id", context.BackgroundJob?.Id);
        activity.SetTag("hangfire.job_type", job?.Type.FullName);
        activity.SetTag("hangfire.job_method", job?.Method.Name);

        context.Items[ActivityKey] = activity;
    }

    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Items.TryGetValue(ActivityKey, out var value) || value is not Activity activity)
        {
            return;
        }

        if (context.Exception is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error);
            activity.SetTag("exception.type", context.Exception.GetType().FullName);
            activity.SetTag("exception.message", context.Exception.Message);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        activity.Dispose();
    }

    /// <summary>
    /// The enqueuer's trace context, or <c>default</c> — which makes the job span a root — when the
    /// job carries none or it does not parse.
    /// </summary>
    private static ActivityContext ReadParent(PerformingContext context)
    {
        var traceParent = context.GetJobParameter<string>(TraceParentParameter);
        if (string.IsNullOrEmpty(traceParent))
        {
            return default;
        }

        var traceState = context.GetJobParameter<string>(TraceStateParameter);
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parent)
            ? parent
            : default;
    }
}
