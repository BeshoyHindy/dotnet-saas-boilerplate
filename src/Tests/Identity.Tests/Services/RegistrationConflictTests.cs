using Boilerplate.Modules.Identity.Services;
using Microsoft.AspNetCore.Identity;
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

    #region Identity's own refusal — the other shape of the same race

    [Fact]
    public void Describe_Should_ReadDuplicateEmail_When_TheValidatorsSawTheWinner()
    {
        // The loser whose validators ran a moment after the winner committed gets no exception from
        // the database at all — just a failed IdentityResult. Same race, different object.
        var kind = RegistrationConflict.Describe(DuplicateResult(nameof(IdentityErrorDescriber.DuplicateEmail)));

        kind.EmailTaken.ShouldBeTrue();
        kind.UserNameTaken.ShouldBeFalse();
        kind.IsDuplicate.ShouldBeTrue();
    }

    [Fact]
    public void Describe_Should_ReadDuplicateUserName_Alone_When_OnlyTheNameCollided()
    {
        // Nobody holds the address, so this is a stranger with the same derived name: the caller
        // must retry under another name, never be handed that stranger's id.
        var kind = RegistrationConflict.Describe(DuplicateResult(nameof(IdentityErrorDescriber.DuplicateUserName)));

        kind.EmailTaken.ShouldBeFalse();
        kind.UserNameTaken.ShouldBeTrue();
    }

    [Fact]
    public void Describe_Should_ReadBoth_When_TheAddressAndTheNameCollided()
    {
        var kind = RegistrationConflict.Describe(DuplicateResult(
            nameof(IdentityErrorDescriber.DuplicateEmail),
            nameof(IdentityErrorDescriber.DuplicateUserName)));

        kind.EmailTaken.ShouldBeTrue();
        kind.UserNameTaken.ShouldBeTrue();
    }

    [Fact]
    public void Describe_Should_IgnoreOtherFailures_When_TheResultIsNotADuplicate()
    {
        // Password policy, invalid characters, a custom validator — none of them is a lost race.
        RegistrationConflict.Describe(DuplicateResult("PasswordTooShort", "InvalidUserName"))
            .IsDuplicate.ShouldBeFalse();
    }

    [Fact]
    public void Describe_Should_MatchOnCode_NotOnTheDescription()
    {
        // The description is localizable prose any IdentityErrorDescriber may reword; the code is the
        // contract. A result whose description SAYS "already taken" under another code is not one.
        var misleading = IdentityResult.Failed(new IdentityError
        {
            Code = "SomethingElseEntirely",
            Description = "Email 'a@b.com' is already taken.",
        });

        RegistrationConflict.Describe(misleading).IsDuplicate.ShouldBeFalse();
    }

    [Fact]
    public void Describe_Should_ReportNothing_When_TheResultSucceeded()
    {
        RegistrationConflict.Describe(IdentityResult.Success).IsDuplicate.ShouldBeFalse();
        RegistrationConflict.Describe((IdentityResult?)null).IsDuplicate.ShouldBeFalse();
    }

    #endregion

    #region Either shape, one answer

    [Fact]
    public void Describe_Should_SeparateTheTwoIndexes_When_GivenAnIndexViolation()
    {
        RegistrationConflict.Describe((Exception)UniqueViolation("EmailIndex"))
            .ShouldBe(new RegistrationConflictKind(EmailTaken: true, UserNameTaken: false));
        RegistrationConflict.Describe((Exception)UniqueViolation("UserNameIndex"))
            .ShouldBe(new RegistrationConflictKind(EmailTaken: false, UserNameTaken: true));
    }

    [Fact]
    public void Describe_Should_ReadTheKind_When_GivenAnIdentityRefusal()
    {
        var exception = new DuplicateUserException(
            "Failed to create user from external principal.",
            DuplicateResult(nameof(IdentityErrorDescriber.DuplicateEmail)));

        RegistrationConflict.Describe((Exception)exception)
            .ShouldBe(new RegistrationConflictKind(EmailTaken: true, UserNameTaken: false));
    }

    [Fact]
    public void Describe_Should_ReportNothing_When_TheFailureIsNeitherShape()
    {
        RegistrationConflict.Describe((Exception)UniqueViolation("IX_Something_Else")).IsDuplicate.ShouldBeFalse();
        RegistrationConflict.Describe(new TimeoutException()).IsDuplicate.ShouldBeFalse();
        RegistrationConflict.Describe((Exception?)null).IsDuplicate.ShouldBeFalse();
    }

    #endregion

    #region Helpers

    private static IdentityResult DuplicateResult(params string[] codes) =>
        IdentityResult.Failed(codes
            .Select(code => new IdentityError { Code = code, Description = $"{code} happened." })
            .ToArray());

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
