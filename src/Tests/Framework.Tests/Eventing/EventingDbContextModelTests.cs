using Boilerplate.BuildingBlocks.Eventing;
using Boilerplate.BuildingBlocks.Eventing.Inbox;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class EventingDbContextModelTests
{
    private static EventingDbContext CreateContext()
    {
        var accessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<AppTenantInfo>(new AppTenantInfo()));

        var options = new DbContextOptionsBuilder<EventingDbContext>()
            .UseNpgsql("Host=arch;Database=arch;Username=arch;Password=arch")
            .Options;

        return new EventingDbContext(accessor, options);
    }

    [Fact]
    public void Maps_OutboxMessages_To_Framework_Schema()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(OutboxMessage));

        entity.ShouldNotBeNull();
        entity!.GetSchema().ShouldBe(EventingConstants.SchemaName);
        entity.GetTableName().ShouldBe("OutboxMessages");
    }

    [Fact]
    public void Outbox_Has_Claim_Index_For_Pending_Scan()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(OutboxMessage));

        entity.ShouldNotBeNull();
        entity!.GetIndexes()
            .Any(i => i.GetDatabaseName() == "IX_OutboxMessages_Pending")
            .ShouldBeTrue("the claim scan filters and orders on these columns under a row lock");
    }

    [Fact]
    public void Maps_InboxMessages_To_Framework_Schema()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(InboxMessage));

        entity.ShouldNotBeNull();
        entity!.GetSchema().ShouldBe(EventingConstants.SchemaName);
        entity.GetTableName().ShouldBe("InboxMessages");
    }

    [Fact]
    public void Outbox_And_Inbox_Are_Not_Tenant_Filtered()
    {
        using var context = CreateContext();

        // Both are IGlobalEntity: the dispatcher scans across tenants within a database.
        //
        // IReadOnlyEntityType.GetQueryFilter() is [Obsolete] in EF Core 10 in favour of the
        // named-filter API (GetDeclaredQueryFilters()), and this repo treats warnings as
        // errors, so we assert the same intent through the non-obsolete surface: neither
        // entity has ANY declared query filter. That is a stronger guarantee than "no
        // anonymous (tenant) filter" — it also confirms ApplyTenantIsolationByDefault
        // skipped these IGlobalEntity types entirely, and that neither implements
        // ISoftDeletable (which would add the named "SoftDelete" filter instead).
        context.Model.FindEntityType(typeof(OutboxMessage))!.GetDeclaredQueryFilters().ShouldBeEmpty();
        context.Model.FindEntityType(typeof(InboxMessage))!.GetDeclaredQueryFilters().ShouldBeEmpty();
    }
}
