using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Eventing.Retention;

/// <summary>
/// Registers <see cref="EventingRetentionJob"/> as a recurring job at start-up. Eventing is not a
/// module, so it has no <c>MapEndpoints</c> to schedule from; this is the same
/// <c>AddOrUpdate</c> call made at host start instead. A host without Hangfire (the DbMigrator, the
/// OpenAPI export) has no <see cref="IRecurringJobManager"/>, and the registration is skipped.
/// </summary>
internal sealed class EventingRetentionScheduler : IHostedService
{
    /// <summary>
    /// Recurring job id. Must not end in <c>-outbox-dispatcher</c>: the host deletes recurring jobs
    /// with that suffix as leftovers of the retired per-module dispatcher.
    /// </summary>
    internal const string RecurringJobId = "eventing-retention";

    private readonly IServiceProvider _services;
    private readonly EventingOptions _options;

    public EventingRetentionScheduler(IServiceProvider services, IOptions<EventingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _services = services;
        _options = options.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var jobManager = _services.GetService<IRecurringJobManager>();
        if (jobManager is null)
        {
            return Task.CompletedTask;
        }

        // Registered even when ProcessedRetentionDays <= 0, like the audit retention job: the run is
        // then a logged no-op, and the schedule never goes stale in Hangfire's storage.
        jobManager.AddOrUpdate(
            RecurringJobId,
            Job.FromExpression<EventingRetentionJob>(j => j.RunAsync(CancellationToken.None)),
            _options.RetentionCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
