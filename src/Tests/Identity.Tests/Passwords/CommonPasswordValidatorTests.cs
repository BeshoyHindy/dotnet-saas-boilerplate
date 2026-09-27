using Boilerplate.Modules.Identity;
using Boilerplate.Modules.Identity.Contracts;
using Boilerplate.Modules.Identity.Domain;
using Boilerplate.Modules.Identity.Passwords;
using Microsoft.AspNetCore.Identity;

namespace Identity.Tests.Passwords;

/// <summary>
/// ASVS 5.0 V6.2.4: a password is checked against a list of common passwords that the policy would
/// otherwise accept. The list ships inside the assembly, so the check never calls out.
/// </summary>
public sealed class CommonPasswordValidatorTests
{
    private readonly CommonPasswordList _list = new();
    private readonly CommonPasswordValidator _sut;

    public CommonPasswordValidatorTests()
    {
        _sut = new CommonPasswordValidator(_list);
    }

    #region Happy Path

    [Theory]
    [InlineData("quietmeadowlantern")]   // 10+ lowercase characters, not on the list
    [InlineData("123Pa$$word!")]         // the integration suites' default password
    public async Task ValidateAsync_Should_Succeed_When_PasswordIsNotOnTheList(string password)
    {
        var result = await _sut.ValidateAsync(null!, new AppUser(), password);

        result.Succeeded.ShouldBeTrue();
    }

    #endregion

    #region Exception

    [Theory]
    [InlineData("password123")]
    [InlineData("PASSWORD123")]          // compared case-insensitively
    [InlineData("Password123")]
    [InlineData("qwertyuiop")]
    [InlineData("1q2w3e4r5t")]
    public async Task ValidateAsync_Should_Fail_When_PasswordIsOnTheList(string password)
    {
        var result = await _sut.ValidateAsync(null!, new AppUser(), password);

        result.Succeeded.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(CommonPasswordValidator.ErrorCode);
        error.Description.ShouldBe(PasswordPolicy.CommonPasswordMessage);
    }

    [Fact]
    public async Task ValidateAsync_Should_NotEchoThePassword_When_ItFails()
    {
        const string password = "password123";

        var result = await _sut.ValidateAsync(null!, new AppUser(), password);

        result.Errors.ShouldAllBe(e => !e.Description.Contains(password, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Edge Cases

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Contains_Should_BeFalse_When_PasswordIsEmpty(string? password)
    {
        // Length is the length validator's job; an empty password is not "common".
        _list.Contains(password).ShouldBeFalse();
    }

    [Fact]
    public void List_Should_HoldAtLeast3000Entries_AllLongEnoughForThePolicy()
    {
        // V6.2.4 asks for at least the top 3000 passwords that match the policy. An entry shorter
        // than the policy's length is dead weight (the length rule already refuses it); if a
        // product lowers PasswordLength, the resource has to be regenerated at the new floor.
        var entries = CommonPasswordList.Entries;

        entries.Count.ShouldBeGreaterThanOrEqualTo(3000);
        entries.ShouldAllBe(e => e.Length >= IdentityModuleConstants.PasswordLength);
    }

    [Fact]
    public void PasswordLength_Should_BeTheContractsFloor()
    {
        // One number: Identity's policy and the tenant admin floor in Multitenancy read the same one.
        IdentityModuleConstants.PasswordLength.ShouldBe(PasswordPolicy.MinimumLength);
    }

    #endregion
}
