using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Globalization;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// What a registration collided with, when it collided with something: the address, the username, or
/// both. Neither flag set means it was not a duplicate at all.
/// </summary>
/// <param name="EmailTaken">Another user in this tenant already holds the address.</param>
/// <param name="UserNameTaken">Another user in this tenant already holds the username.</param>
internal readonly record struct RegistrationConflictKind(bool EmailTaken, bool UserNameTaken)
{
    public bool IsDuplicate => EmailTaken || UserNameTaken;
}

/// <summary>
/// Reads a failed registration and decides whether it was a lost race for the address, the username,
/// or neither (#86).
///
/// <para>A race is refused in one of **two** shapes, and a fix that knows only one of them is a fix
/// that works under `dotnet test` and fails in production. If the loser's write reaches the database
/// first, the unique index refuses it — a <see cref="DbUpdateException"/> wrapping `23505`. If the
/// winner commits a moment earlier, ASP.NET Identity's own pre-insert validators see the winner and
/// refuse it themselves — an <see cref="IdentityResult"/> carrying `DuplicateEmail` /
/// `DuplicateUserName`, no exception in sight. Same race, same second, two entirely different
/// objects to catch.</para>
///
/// <para>It is deliberately narrow in both. A `23505` proves only that *some* unique index was
/// violated, and answering "that e-mail is already taken" to a collision on, say, a future index over
/// something else would be a confident lie — one that also hides a real bug behind a 400. So only the
/// two indexes registration can race on are recognised, and only Identity's two duplicate codes
/// (matched on <see cref="IdentityError.Code"/>, never on the human-readable description, which is
/// localizable and rewritable by any <see cref="IdentityErrorDescriber"/>); everything else stays the
/// failure it is.</para>
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

    /// <summary>
    /// What ASP.NET Identity refused a create for — the shape a loser gets when the winner committed
    /// a moment before its validators ran. Matched on the error <b>code</b>: the description is
    /// localizable prose and a custom <see cref="IdentityErrorDescriber"/> may reword it freely.
    /// </summary>
    public static RegistrationConflictKind Describe(IdentityResult? result)
    {
        if (result is null || result.Succeeded)
        {
            return default;
        }

        var emailTaken = false;
        var userNameTaken = false;

        foreach (var error in result.Errors)
        {
            emailTaken |= string.Equals(
                error.Code, nameof(IdentityErrorDescriber.DuplicateEmail), StringComparison.Ordinal);
            userNameTaken |= string.Equals(
                error.Code, nameof(IdentityErrorDescriber.DuplicateUserName), StringComparison.Ordinal);
        }

        return new RegistrationConflictKind(emailTaken, userNameTaken);
    }

    /// <summary>
    /// What a failed registration collided with, whichever of the two shapes it arrived in. Anything
    /// that is not a recognised duplicate comes back with neither flag set, and the caller rethrows.
    /// </summary>
    public static RegistrationConflictKind Describe(Exception? exception) => exception switch
    {
        DuplicateUserException duplicate => duplicate.Kind,
        DbUpdateException update when IsDuplicateUser(update) =>
            (update.InnerException as PostgresException)?.ConstraintName switch
            {
                UserNameIndex => new RegistrationConflictKind(EmailTaken: false, UserNameTaken: true),
                _ => new RegistrationConflictKind(EmailTaken: true, UserNameTaken: false),
            },
        _ => default,
    };
}
