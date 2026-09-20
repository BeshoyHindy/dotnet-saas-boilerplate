using System.ComponentModel.DataAnnotations;

namespace Boilerplate.BuildingBlocks.Shared.Persistence;

/// <summary>
/// Configuration options for database provider selection and connection information.
/// </summary>
public sealed class DatabaseOptions : IValidatableObject
{
    /// <summary>
    /// The database provider to use. The only valid value is <see cref="DbProviders.PostgreSQL"/>.
    /// Defaults to PostgreSQL.
    /// </summary>
    public string Provider { get; set; } = DbProviders.PostgreSQL;

    /// <summary>
    /// The connection string used by EF Core DbContexts and related services.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The assembly that contains EF Core migrations for the selected provider.
    /// </summary>
    public string MigrationsAssembly { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            yield return new ValidationResult("connection string cannot be empty.", new[] { nameof(ConnectionString) });
        }
    }
}