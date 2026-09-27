using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts;
using Boilerplate.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;

namespace Integration.Tests.Tests.Authentication;

/// <summary>
/// The password policy is ASVS 5.0 L1: a length floor (V6.2.1), no composition rules (V6.2.5) and a
/// bundled common-password list (V6.2.4). Every path that sets a password — registration, reset and
/// change — goes through <see cref="UserManager{TUser}"/>, so each one is exercised here end to end.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class PasswordPolicyTests
{
    // On the bundled list, and long enough that only the list can be what refuses it.
    private const string CommonPassword = "password123";

    // Ten lowercase letters, not on the list: what the old digit/upper/lower rules refused.
    private const string LowercasePassphrase = "quietmeadowlantern";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public PasswordPolicyTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    #region Registration

    [Fact]
    public async Task Register_Should_Return400WithTheGenericMessage_When_PasswordIsCommon()
    {
        using var client = _factory.CreateClient();

        var response = await RegisterAsync(client, CommonPassword);

        await ShouldBeRejectedAsCommonAsync(response);
    }

    [Fact]
    public async Task Register_Should_Succeed_When_PasswordIsAnAllLowercasePassphrase()
    {
        using var client = _factory.CreateClient();

        var response = await RegisterAsync(client, LowercasePassphrase);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    #endregion

    #region Reset

    [Fact]
    public async Task ResetPassword_Should_Return400WithTheGenericMessage_When_PasswordIsCommon()
    {
        // Arrange
        var email = $"policy_reset_{Guid.NewGuid():N}@test.com";
        await CreateActiveUserAsync(email, TestConstants.DefaultPassword);
        var encodedToken = await GenerateResetTokenAsync(email);
        using var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.RootAuthBasePath}/reset-password",
            new { email, password = CommonPassword, token = encodedToken });

        // Assert — refused, and the old password still works.
        await ShouldBeRejectedAsCommonAsync(response);
        var token = await _auth.GetTokenAsync(email, TestConstants.DefaultPassword, TestConstants.RootTenantId);
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ResetPassword_Should_Succeed_When_PasswordIsAnAllLowercasePassphrase()
    {
        // Arrange
        var email = $"policy_reset_ok_{Guid.NewGuid():N}@test.com";
        await CreateActiveUserAsync(email, TestConstants.DefaultPassword);
        var encodedToken = await GenerateResetTokenAsync(email);
        using var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.RootAuthBasePath}/reset-password",
            new { email, password = LowercasePassphrase, token = encodedToken });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var token = await _auth.GetTokenAsync(email, LowercasePassphrase, TestConstants.RootTenantId);
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    #region Change

    [Fact]
    public async Task ChangePassword_Should_Return400WithTheGenericMessage_When_NewPasswordIsCommon()
    {
        // Arrange
        var email = $"policy_change_{Guid.NewGuid():N}@test.com";
        await CreateActiveUserAsync(email, TestConstants.DefaultPassword);
        using var client = await _auth.CreateAuthenticatedClientAsync(
            email, TestConstants.DefaultPassword, TestConstants.RootTenantId);

        // Act
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/change-password",
            new { password = TestConstants.DefaultPassword, newPassword = CommonPassword, confirmNewPassword = CommonPassword });

        // Assert — refused, and the old password still works.
        await ShouldBeRejectedAsCommonAsync(response);
        var token = await _auth.GetTokenAsync(email, TestConstants.DefaultPassword, TestConstants.RootTenantId);
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    #region Helpers

    private static async Task ShouldBeRejectedAsCommonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain(PasswordPolicy.CommonPasswordMessage);
        body.ShouldNotContain(CommonPassword, Case.Insensitive);
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string password)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        return client.PostAsJsonAsync($"{TestConstants.RootAuthBasePath}/register", new
        {
            firstName = "Policy",
            lastName = "Check",
            email = $"policy-{uniqueId}@example.com",
            userName = $"policy-{uniqueId}",
            password,
            confirmPassword = password
        });
    }

    // Set the tenant context INLINE, not via an awaited helper: the Finbuckle setter writes an AsyncLocal
    // that's lost on return from a separate method, NREing the tenant query filter during CreateAsync.
    private async Task CreateActiveUserAsync(string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            FirstName = "Policy",
            LastName = "User",
            Email = email,
            UserName = email.Split('@')[0],
            EmailConfirmed = true,
            IsActive = true
        };
        var result = await userManager.CreateAsync(user, password);
        result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    private async Task<string> GenerateResetTokenAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByEmailAsync(email);
        user.ShouldNotBeNull();
        var raw = await userManager.GeneratePasswordResetTokenAsync(user);
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
    }

    #endregion
}
