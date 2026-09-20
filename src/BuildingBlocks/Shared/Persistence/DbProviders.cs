namespace Boilerplate.BuildingBlocks.Shared.Persistence;

/// <summary>
/// Supported database providers for the starter kit. PostgreSQL is the only one (ADR-0003).
/// </summary>
public static class DbProviders
{
    /// <summary>
    /// PostgreSQL database provider.
    /// </summary>
    public const string PostgreSQL = "POSTGRESQL";
}