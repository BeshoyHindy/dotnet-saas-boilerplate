namespace Boilerplate.Modules.Identity.Data;

/// <summary>
/// Builds the <c>ILIKE</c> pattern for a case-insensitive "contains" search.
/// </summary>
/// <remarks>
/// Search goes through <c>EF.Functions.ILike(column, pattern, EscapeCharacter)</c> on the raw column,
/// because that is the shape the <c>pg_trgm</c> GIN indexes on the searched columns serve.
/// <c>column.ToLower().Contains(term)</c> compiles to <c>LOWER(column) LIKE …</c>, which no index on
/// <c>column</c> can serve, so it scanned every row the tenant filter left. The term's own
/// <c>%</c>, <c>_</c> and <c>\</c> are escaped so it still matches literally, as <c>Contains</c> did.
/// </remarks>
internal static class ContainsPattern
{
    public const string EscapeCharacter = "\\";

    public static string For(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var escaped = term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }
}
