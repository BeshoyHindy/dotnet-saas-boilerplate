using Boilerplate.BuildingBlocks.Eventing;
using Boilerplate.BuildingBlocks.Eventing.Inbox;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Eventing.Retention;
using Hangfire;
using Hangfire.Storage;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace Integration.Tests.Tests.Eventing;

/// <summary>
/// The dispatcher only stamps an outbox row processed and the inbox keeps one row per handled
/// event, so without the purge both tables grow for the life of the installation. What matters is
/// "only what is processed and past the window": a pending row is still owed a dispatch and a
/// dead-lettered one is what a redrive recovers, however old either is.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class EventingRetentionJobTests
{
    private readonly AppWebApplicationFactory _factory;

    public EventingRetentionJobTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Purge_Should_Delete_Old_Processed_Rows_And_Keep_Pending_Dead_And_Recent_Ones()
    {
        var marker = $"retention-{Guid.CreateVersion7():N}";
        var now = DateTime.UtcNow;

        var oldProcessed = await SeedOutboxAsync(marker, processedOnUtc: now.AddDays(-30));
        var recentProcessed = await SeedOutboxAsync(marker, processedOnUtc: now.AddDays(-1));
        var oldPending = await SeedOutboxAsync(marker, processedOnUtc: null, createdOnUtc: now.AddDays(-30));
        var oldDead = await SeedOutboxAsync(marker, processedOnUtc: null, createdOnUtc: now.AddDays(-30), isDead: true);
        var oldInbox = await SeedInboxAsync(marker, processedOnUtc: now.AddDays(-30));
        var recentInbox = await SeedInboxAsync(marker, processedOnUtc: now.AddDays(-1));

        // Batch size 1 forces the loop to run more than once, so batching is exercised too.
        await RunJobAsync(new EventingOptions { ProcessedRetentionDays = 7, RetentionDeleteBatchSize = 1 });

        var outbox = await OutboxIdsAsync(marker);
        outbox.ShouldNotContain(oldProcessed, "a processed row past the window is what the purge exists to remove");
        outbox.ShouldContain(recentProcessed, "a processed row inside the window is kept");
        outbox.ShouldContain(oldPending, "an unprocessed row is still owed a dispatch, however old");
        outbox.ShouldContain(oldDead, "a dead-lettered row is what a redrive recovers, so it is never purged");

        var inbox = await InboxIdsAsync(marker);
        inbox.ShouldNotContain(oldInbox, "an inbox row past the window is removed");
        inbox.ShouldContain(recentInbox, "an inbox row inside the window still deduplicates redeliveries");
    }

    [Fact]
    public async Task Purge_Should_Do_Nothing_When_Retention_Is_Off()
    {
        var marker = $"retention-{Guid.CreateVersion7():N}";
        var oldProcessed = await SeedOutboxAsync(marker, processedOnUtc: DateTime.UtcNow.AddDays(-30));
        var oldInbox = await SeedInboxAsync(marker, processedOnUtc: DateTime.UtcNow.AddDays(-30));

        await RunJobAsync(new EventingOptions { ProcessedRetentionDays = 0 });

        (await OutboxIdsAsync(marker)).ShouldContain(oldProcessed, "a zero window switches the purge off");
        (await InboxIdsAsync(marker)).ShouldContain(oldInbox, "a zero window switches the purge off");
    }

    [Fact]
    public void Purge_Should_Be_Scheduled_As_A_Recurring_Job_At_Startup()
    {
        using var connection = _factory.Services.GetRequiredService<JobStorage>().GetConnection();
        var job = connection.GetRecurringJobs().SingleOrDefault(j => j.Id == "eventing-retention");

        job.ShouldNotBeNull("without the schedule the purge never runs and both tables grow unbounded");
        job.Job.Type.ShouldBe(typeof(EventingRetentionJob));
    }

    // ─── helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs the job the way Hangfire does for a <c>[SystemJob]</c>: constructed in a fresh scope with
    /// no tenant ambient.
    /// </summary>
    private async Task RunJobAsync(EventingOptions options)
    {
        using var scope = _factory.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<EventingRetentionJob>(scope.ServiceProvider, Options.Create(options));
        await job.RunAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedOutboxAsync(
        string marker,
        DateTime? processedOnUtc,
        DateTime? createdOnUtc = null,
        bool isDead = false)
    {
        var id = Guid.CreateVersion7();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            CreatedOnUtc = createdOnUtc ?? processedOnUtc ?? DateTime.UtcNow,
            Type = marker,
            Payload = "{}",
            TenantId = TestConstants.RootTenantId,
            ProcessedOnUtc = processedOnUtc,
            IsDead = isDead,
            RetryCount = isDead ? 5 : 0,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedInboxAsync(string marker, DateTime processedOnUtc)
    {
        var id = Guid.CreateVersion7();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        db.InboxMessages.Add(new InboxMessage
        {
            Id = id,
            EventType = marker,
            HandlerName = "RetentionTestHandler",
            ProcessedOnUtc = processedOnUtc,
            TenantId = TestConstants.RootTenantId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<List<Guid>> OutboxIdsAsync(string marker)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EventingDbContext>()
            .OutboxMessages.AsNoTracking().Where(o => o.Type == marker).Select(o => o.Id).ToListAsync();
    }

    private async Task<List<Guid>> InboxIdsAsync(string marker)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EventingDbContext>()
            .InboxMessages.AsNoTracking().Where(i => i.EventType == marker).Select(i => i.Id).ToListAsync();
    }
}
