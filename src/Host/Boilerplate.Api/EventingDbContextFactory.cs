using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Boilerplate.Api;

/// <summary>
/// Design-time factory for <see cref="EventingDbContext"/>. `dotnet ef migrations add` builds the
/// model straight from this factory, so it never goes through <c>AddHeroPlatform</c>'s strict
/// <c>DatabaseOptions</c> validation (which requires a real connection string) or the DI container.
/// The fallback connection string below is never dialed — no query runs at design time.
///
/// `dotnet ef` only discovers <see cref="IDesignTimeDbContextFactory{TContext}"/> implementations in
/// the assembly that declares the DbContext or in <c>--startup-project</c>'s assembly — not in the
/// <c>--project</c> (migrations) assembly. <see cref="EventingDbContext"/> lives in
/// <c>src/BuildingBlocks/Eventing</c>, which is shared by every module and not touched without
/// explicit approval (see buildingblocks-protection.md), so this factory lives here instead: the
/// startup project already references <c>BuildingBlocks.Eventing</c> (<c>AddEventingCore</c>).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated by `dotnet ef` via reflection, not DI")]
internal sealed class EventingDbContextFactory : IDesignTimeDbContextFactory<EventingDbContext>
{
    public EventingDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var configuredConnectionString = configuration["DatabaseOptions:ConnectionString"];
        var connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
            ? "Host=localhost;Database=boilerplate-eventing;Username=postgres;Password=postgres"
            : configuredConnectionString;
        var migrationsAssembly = configuration["DatabaseOptions:MigrationsAssembly"]
            ?? "Boilerplate.Migrations.PostgreSQL";

        var optionsBuilder = new DbContextOptionsBuilder<EventingDbContext>()
            .UseNpgsql(connectionString, b => b.MigrationsAssembly(migrationsAssembly));

        return new EventingDbContext(new AsyncLocalMultiTenantContextAccessor<AppTenantInfo>(), optionsBuilder.Options);
    }
}
