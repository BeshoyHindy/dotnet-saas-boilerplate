using Boilerplate.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Boilerplate.BuildingBlocks.Eventing.Persistence;

/// <summary>
/// Migrates the framework <c>framework</c> schema that owns the outbox and inbox
/// tables. Like every other <see cref="IDbInitializer"/> it is idempotent, so the
/// migrator and the provisioning Migrations step can both run it.
/// </summary>
public sealed partial class EventingDbInitializer : IDbInitializer
{
    private readonly EventingDbContext _context;
    private readonly ILogger<EventingDbInitializer> _logger;

    public EventingDbInitializer(EventingDbContext context, ILogger<EventingDbInitializer> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if ((await _context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            await _context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            LogMigrated(_context.TenantInfo?.Identifier);
        }
    }

    public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Tenant}] applied database migrations for the eventing schema")]
    private partial void LogMigrated(string? tenant);
}
