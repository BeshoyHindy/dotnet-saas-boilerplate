using Boilerplate.BuildingBlocks.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Boilerplate.BuildingBlocks.Persistence;

/// <summary>
/// Validates database connection strings for the supported provider (PostgreSQL).
/// </summary>
/// <param name="dbSettings">Database configuration options.</param>
/// <param name="logger">Logger instance for error tracking.</param>
public sealed class ConnectionStringValidator(IOptions<DatabaseOptions> dbSettings, ILogger<ConnectionStringValidator> logger) : IConnectionStringValidator
{
    private readonly DatabaseOptions _dbSettings = dbSettings.Value;
    private readonly ILogger<ConnectionStringValidator> _logger = logger;

    public bool TryValidate(string connectionString, string? dbProvider = null)
    {
        if (string.IsNullOrWhiteSpace(dbProvider))
        {
            dbProvider = _dbSettings.Provider;
        }

        // Fail closed: PostgreSQL is the only supported provider, so anything else is a
        // misconfiguration rather than a string we simply cannot parse.
        if (!string.Equals(dbProvider, DbProviders.PostgreSQL, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Database Provider {Provider} is not supported. Only {Supported} is supported.",
                dbProvider,
                DbProviders.PostgreSQL);
            return false;
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (ArgumentException ex)
        {
            // NpgsqlConnectionStringBuilder throws ArgumentException for malformed strings.
            _logger.LogError(ex, "Connection String Validation Exception : {Error}", ex.Message);
            return false;
        }
        catch (FormatException ex)
        {
            // Catches format-related parsing failures in connection string values.
            _logger.LogError(ex, "Connection String Validation Exception : {Error}", ex.Message);
            return false;
        }
    }
}