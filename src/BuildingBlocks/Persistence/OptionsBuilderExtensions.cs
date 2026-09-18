using System.Data.Common;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Boilerplate.BuildingBlocks.Persistence;

/// <summary>
/// Extension methods for configuring Entity Framework DbContextOptionsBuilder.
/// </summary>
public static class OptionsBuilderExtensions
{
    /// <summary>
    /// Configures the database provider and connection for the Hero framework.
    /// </summary>
    /// <param name="builder">The DbContextOptionsBuilder to configure.</param>
    /// <param name="dbProvider">The database provider (PostgreSQL).</param>
    /// <param name="connectionString">The database connection string.</param>
    /// <param name="migrationsAssembly">The assembly containing database migrations.</param>
    /// <param name="isDevelopment">Whether the application is running in development mode.</param>
    /// <returns>The configured DbContextOptionsBuilder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when builder is null or dbProvider is null/whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when an unsupported database provider is specified.</exception>
    public static DbContextOptionsBuilder ConfigureHeroDatabase(
        this DbContextOptionsBuilder builder,
        string dbProvider,
        string connectionString,
        string migrationsAssembly,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(dbProvider);

        ConfigureCommon(builder, isDevelopment);

        if (!string.Equals(dbProvider, DbProviders.PostgreSQL, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Database Provider {dbProvider} is not supported. Only {DbProviders.PostgreSQL} is supported.");
        }

        builder.UseNpgsql(connectionString, e =>
        {
            e.MigrationsAssembly(migrationsAssembly);
        });

        return builder;
    }

    /// <summary>
    /// Configures the provider against an existing <see cref="DbConnection"/> owned by the DI scope
    /// rather than a connection string.
    ///
    /// Every context in a scope sharing one connection object is what allows the outbox write to
    /// join the business transaction — EF Core can only enlist a context in an existing transaction
    /// when both contexts sit on the same connection.
    /// </summary>
    public static DbContextOptionsBuilder ConfigureHeroDatabase(
        this DbContextOptionsBuilder builder,
        string dbProvider,
        DbConnection connection,
        string migrationsAssembly,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(dbProvider);

        ConfigureCommon(builder, isDevelopment);

        if (!string.Equals(dbProvider, DbProviders.PostgreSQL, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Database Provider {dbProvider} is not supported. Only {DbProviders.PostgreSQL} is supported.");
        }

        // contextOwnsConnection: false — the scope disposes it, not the first context to finish.
        builder.UseNpgsql(connection, contextOwnsConnection: false, e =>
        {
            e.MigrationsAssembly(migrationsAssembly);
        });

        return builder;
    }

    private static void ConfigureCommon(DbContextOptionsBuilder builder, bool isDevelopment)
    {
        builder.ConfigureWarnings(warnings =>
            warnings.Log(RelationalEventId.PendingModelChangesWarning));

        if (isDevelopment)
        {
            builder.EnableSensitiveDataLogging();
            builder.EnableDetailedErrors();
        }
    }
}