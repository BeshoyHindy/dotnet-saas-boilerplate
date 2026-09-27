using Boilerplate.Modules.Auditing.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Auditing.Tests.Persistence;

/// <summary>
/// <see cref="EntityDiffBuilder"/> against a real EF Core change tracker (in-memory SQLite), because
/// what it reads off <c>PropertyEntry</c> is exactly what a hand-built fake would get wrong. Pins
/// #103: a sensitive property's old/new value is replaced with <c>****</c> — not merely flagged — and
/// a null value is never turned into one.
/// </summary>
public sealed class EntityDiffBuilderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Widget _tracked;

    public EntityDiffBuilderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Context = new WidgetDbContext(new DbContextOptionsBuilder<WidgetDbContext>()
            .UseSqlite(_connection)
            .Options);
        Context.Database.EnsureCreated();

        _tracked = new Widget { Id = 1, Name = "Alice", PasswordHash = "h1", SecurityStamp = "s1" };
        Context.Widgets.Add(_tracked);
        Context.SaveChanges();
    }

    private WidgetDbContext Context { get; }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Build_Should_Mask_SecurityStamp_When_Changed()
    {
        // Arrange
        _tracked.SecurityStamp = "s2";

        // Act
        var diffs = EntityDiffBuilder.Build(Context.ChangeTracker.Entries());

        // Assert
        var change = diffs.Single().Changes.Single(c => c.Name == nameof(Widget.SecurityStamp));
        change.IsSensitive.ShouldBeTrue();
        change.OldValue.ShouldBe("****");
        change.NewValue.ShouldBe("****");
    }

    [Fact]
    public void Build_Should_Mask_PasswordHash_When_Changed()
    {
        // Arrange
        _tracked.PasswordHash = "h2";

        // Act
        var diffs = EntityDiffBuilder.Build(Context.ChangeTracker.Entries());

        // Assert
        var change = diffs.Single().Changes.Single(c => c.Name == nameof(Widget.PasswordHash));
        change.IsSensitive.ShouldBeTrue();
        change.OldValue.ShouldBe("****");
        change.NewValue.ShouldBe("****");
    }

    [Fact]
    public void Build_Should_KeepNullAsNull_When_SensitivePropertyIsAdded()
    {
        // Arrange — an insert with no PasswordHash set yet: nothing to mask.
        var inserted = new Widget { Id = 2, Name = "Bob" };
        Context.Widgets.Add(inserted);

        // Act
        var diffs = EntityDiffBuilder.Build(Context.ChangeTracker.Entries());

        // Assert
        var change = diffs.Single(d => d.Key == "Id:2").Changes.Single(c => c.Name == nameof(Widget.PasswordHash));
        change.IsSensitive.ShouldBeTrue();
        change.OldValue.ShouldBeNull();
        change.NewValue.ShouldBeNull();
    }

    [Fact]
    public void Build_Should_NotMask_OrdinaryProperty_When_Changed()
    {
        // Arrange
        _tracked.Name = "Alicia";

        // Act
        var diffs = EntityDiffBuilder.Build(Context.ChangeTracker.Entries());

        // Assert
        var change = diffs.Single().Changes.Single(c => c.Name == nameof(Widget.Name));
        change.IsSensitive.ShouldBeFalse();
        change.OldValue.ShouldBe("Alice");
        change.NewValue.ShouldBe("Alicia");
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public string? PasswordHash { get; set; }

        public string? SecurityStamp { get; set; }
    }

    private sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
    }
}
