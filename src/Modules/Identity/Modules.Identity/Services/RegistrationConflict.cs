using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Globalization;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// Reads a failed registration write and decides whether it was a lost race to one of the user
/// table's unique indexes (#86).
///
/// It is deliberately narrow. A `23505` proves only that *some* unique index was violated, and
/// answering "that e-mail is already taken" to a collision on, say, a future index over something
/// else would be a confident lie — one that also hides a real bug behind a 400. So only the two
/// indexes registration can actually race on are recognised; everything else stays the 500 it is.
/// </summary>
internal static class RegistrationConflict
{
    /// <summary>The tenant-scoped unique index on <c>NormalizedEmail</c>.</summary>
    internal const string EmailIndex = "EmailIndex";

    /// <summary>The tenant-scoped unique index on <c>NormalizedUserName</c>, widened by Finbuckle.</summary>
    internal const string UserNameIndex = "UserNameIndex";

    /// <summary>
    /// True when <paramref name="exception"/> is a unique violation on one of the two user indexes
    /// — the only shape a concurrent duplicate registration can take.
    /// </summary>
    public static bool IsDuplicateUser(DbUpdateException? exception) =>
        exception?.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation
        && (string.Equals(violation.ConstraintName, EmailIndex, StringComparison.Ordinal)
            || string.Equals(violation.ConstraintName, UserNameIndex, StringComparison.Ordinal));

    /// <summary>
    /// The reason line a lost race gets, worded exactly like the one ASP.NET Identity's pre-insert
    /// check produces for the same collision — the caller cannot tell which of the two refused them,
    /// and has no reason to care.
    /// </summary>
    /// <param name="exception">A violation <see cref="IsDuplicateUser"/> has already accepted.</param>
    /// <param name="email">The address the caller tried to register.</param>
    /// <param name="userName">The username the caller tried to register.</param>
    public static string DuplicateReasonFor(DbUpdateException? exception, string email, string userName)
    {
        var constraint = (exception?.InnerException as PostgresException)?.ConstraintName;

        return string.Equals(constraint, UserNameIndex, StringComparison.Ordinal)
            ? string.Format(CultureInfo.InvariantCulture, "User name '{0}' is already taken.", userName)
            : string.Format(CultureInfo.InvariantCulture, "Email '{0}' is already taken.", email);
    }
}
