using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Boilerplate.Modules.Files.Data;

/// <summary>
/// Design-time factory for <see cref="FilesDbContext"/>. `dotnet ef migrations add` builds the
/// model straight from this factory, so it never goes through <c>AddHeroPlatform</c>'s strict
/// <c>DatabaseOptions</c> validation (which requires a real connection string) or the DI container.
/// The fallback connection string below is never dialed — no query runs at design time.
/// </summary>
public sealed class FilesDbContextFactory : IDesignTimeDbContextFactory<FilesDbContext>
{
    public FilesDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var configuredConnectionString = configuration["DatabaseOptions:ConnectionString"];
        var connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
            ? "Host=localhost;Database=boilerplate-files;Username=postgres;Password=postgres"
            : configuredConnectionString;
        var migrationsAssembly = configuration["DatabaseOptions:MigrationsAssembly"]
            ?? "Boilerplate.Migrations.PostgreSQL";

        var optionsBuilder = new DbContextOptionsBuilder<FilesDbContext>()
            .UseNpgsql(connectionString, b => b.MigrationsAssembly(migrationsAssembly));

        return new FilesDbContext(new AsyncLocalMultiTenantContextAccessor<AppTenantInfo>(), optionsBuilder.Options);
    }
}
