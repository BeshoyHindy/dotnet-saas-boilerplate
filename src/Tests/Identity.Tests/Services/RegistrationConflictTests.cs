using Boilerplate.Modules.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Identity.Tests.Services;

/// <summary>
/// The 23505 → 400 mapping (#86). A unique violation on one of the user table's two indexes is the
/// caller's duplicate and must read exactly like the pre-insert check's refusal; a unique violation
/// anywhere else is a bug, and answering "that e-mail is already taken" to it would be a confident
/// lie that also hides the bug behind a 400.
/// </summary>
public sealed class RegistrationConflictTests
{
    private const string Email = "racer@example.com";
    private const string UserName = "racer";

    #region Recognised collisions

    [Theory]
    [InlineData("EmailIndex")]
    [InlineData("UserNameIndex")]
    public void IsDuplicateUser_Should_BeTrue_When_AUserIndexIsViolated(string constraint)
    {
        RegistrationConflict.IsDuplicateUser(UniqueViolation(constraint)).ShouldBeTrue();
    }

    [Fact]
    public void DuplicateReasonFor_Should_NameTheEmail_When_TheEmailIndexIsViolated()
    {
        RegistrationConflict.DuplicateReasonFor(UniqueViolation("EmailIndex"), Email, UserName)
            .ShouldBe($"Email '{Email}' is already taken.");
    }

    [Fact]
    public void DuplicateReasonFor_Should_NameTheUserName_When_TheUserNameIndexIsViolated()
    {
        RegistrationConflict.DuplicateReasonFor(UniqueViolation("UserNameIndex"), Email, UserName)
            .ShouldBe($"User name '{UserName}' is already taken.");
    }

    #endregion

    #region Everything else stays a fault

    [Fact]
    public void IsDuplicateUser_Should_BeFalse_When_AnotherIndexIsViolated()
    {
        // A future unique index — on a session token, a group name, anything. It is not a duplicate
        // registration, so it must escape as the 500 it is.
        RegistrationConflict.IsDuplicateUser(UniqueViolation("IX_UserSessions_TokenHash"))
            .ShouldBeFalse();
    }

    [Fact]
    public void IsDuplicateUser_Should_BeFalse_When_TheConstraintIsUnnamed()
    {
        RegistrationConflict.IsDuplicateUser(UniqueViolation(constraintName: null)).ShouldBeFalse();
    }

    [Fact]
    public void IsDuplicateUser_Should_BeFalse_When_TheViolationIsNotUnique()
    {
        // 23503 — foreign key. Same family of error, entirely different meaning.
        RegistrationConflict
            .IsDuplicateUser(PostgresFailure("23503", "FK_UserGroups_Users_UserId"))
            .ShouldBeFalse();
    }

    [Fact]
    public void IsDuplicateUser_Should_BeFalse_When_TheFailureIsNotFromPostgres()
    {
        RegistrationConflict
            .IsDuplicateUser(new DbUpdateException("save failed", new TimeoutException()))
            .ShouldBeFalse();
    }

    [Fact]
    public void IsDuplicateUser_Should_BeFalse_When_ThereIsNoException()
    {
        RegistrationConflict.IsDuplicateUser(null).ShouldBeFalse();
    }

    #endregion

    #region Helpers

    private static DbUpdateException UniqueViolation(string? constraintName) =>
        PostgresFailure(PostgresErrorCodes.UniqueViolation, constraintName);

    private static DbUpdateException PostgresFailure(string sqlState, string? constraintName) =>
        new(
            "An error occurred while saving the entity changes.",
            new PostgresException(
                messageText: "duplicate key value violates unique constraint",
                severity: "ERROR",
                invariantSeverity: "ERROR",
                sqlState: sqlState,
                constraintName: constraintName));

    #endregion
}
