using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Auditing.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Auditing.Tests.Persistence;

/// <summary>
/// <see cref="AuditingSaveChangesInterceptor"/> against a real SaveChanges pipeline (in-memory
/// SQLite): validates the <see cref="IAuditExempt"/> opt-out — a matching entity is skipped entirely,
/// with no EntityChange event published at all, as opposed to a sensitive property, which is
/// published but masked (<see cref="EntityDiffBuilderTests"/>).
/// </summary>
public sealed class AuditingSaveChangesInterceptorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IAuditPublisher _publisher = Substitute.For<IAuditPublisher>();
    private readonly WidgetDbContext _context;

    public AuditingSaveChangesInterceptorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var interceptor = new AuditingSaveChangesInterceptor(_publisher, TimeProvider.System);
        _context = new WidgetDbContext(new DbContextOptionsBuilder<WidgetDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task SavingChangesAsync_Should_Publish_When_EntityIsNotExempt()
    {
        // Arrange
        _context.Widgets.Add(new Widget { Id = 1, Name = "Alice" });

        // Act
        await _context.SaveChangesAsync();

        // Assert
        await _publisher.Received(1).PublishAsync(
            Arg.Is<IAuditEvent>(e => e.EventType == AuditEventType.EntityChange), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SavingChangesAsync_Should_NotPublish_When_EntityIsAuditExempt()
    {
        // Arrange
        _context.ExemptWidgets.Add(new ExemptWidget { Id = 1, Name = "Alice" });

        // Act
        await _context.SaveChangesAsync();

        // Assert — no EntityChange event at all, not even a masked one.
        await _publisher.DidNotReceive().PublishAsync(Arg.Any<IAuditEvent>(), Arg.Any<CancellationToken>());
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class ExemptWidget : IAuditExempt
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        public DbSet<ExemptWidget> ExemptWidgets => Set<ExemptWidget>();
    }
}
