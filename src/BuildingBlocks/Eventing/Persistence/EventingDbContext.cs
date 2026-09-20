using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Inbox;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Persistence.Context;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Eventing.Persistence;

/// <summary>
/// The single context owning the transactional outbox and inbox.
///
/// Derives from <see cref="BaseDbContext"/>, so it shares the DI scope's connection
/// with the module context that wrote the business data and joins that transaction.
/// That is what makes the outbox transactional.
///
/// Owning these tables here — rather than once per module DbContext — is what
/// keeps <c>IOutboxStore</c>/<c>IInboxStore</c> to a single, unambiguous DI
/// registration (issue #1349).
/// </summary>
public class EventingDbContext : BaseDbContext
{
    public EventingDbContext(
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        DbContextOptions<EventingDbContext> options,
        IOptions<DatabaseOptions> settings,
        IHostEnvironment environment)
        : base(multiTenantContextAccessor, options, settings, environment)
    {
    }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration(EventingConstants.SchemaName));
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration(EventingConstants.SchemaName));

        // Must run last: ApplyTenantIsolationByDefault inspects the configured entities.
        base.OnModelCreating(modelBuilder);
    }
}
