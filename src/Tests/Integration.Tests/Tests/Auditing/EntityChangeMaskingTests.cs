using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;

namespace Integration.Tests.Tests.Auditing;

/// <summary>
/// #103: a self-service password change writes an entity-change audit row for the user, and the row
/// carries no clear-text credential. The interceptor (unit-tested in Auditing.Tests against a real EF
/// change tracker) masks anything <c>SensitiveFieldNames</c> matches; this proves it end to end through
/// the real HTTP + Identity + Auditing pipeline.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class EntityChangeMaskingTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public EntityChangeMaskingTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task ChangePassword_Should_WriteAuditRow_Without_HashOrStampInClearText()
    {
        // Arrange
        var email = $"maskaudit_{Guid.NewGuid():N}@test.com";
        const string oldPassword = TestConstants.DefaultPassword;
        const string newPassword = "NewPa$$word123!";
        var (userId, oldPasswordHash) = await CreateActiveUserAsync(email, oldPassword);

        using var client = await _auth.CreateAuthenticatedClientAsync(email, oldPassword, TestConstants.RootTenantId);
        using var adminClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/change-password",
            new { password = oldPassword, newPassword, confirmNewPassword = newPassword });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await PollForUserEntityChangeAsync(adminClient, userId);

        // Assert
        var changes = payload.GetProperty("changes").EnumerateArray().ToList();

        var passwordHashChange = changes.Single(c => c.GetProperty("name").GetString() == "PasswordHash");
        passwordHashChange.GetProperty("oldValue").GetString().ShouldBe("****");
        passwordHashChange.GetProperty("newValue").GetString().ShouldBe("****");

        var securityStampChange = changes.SingleOrDefault(
            c => c.GetProperty("name").GetString() == "SecurityStamp");
        if (securityStampChange.ValueKind != JsonValueKind.Undefined)
        {
            securityStampChange.GetProperty("newValue").GetString().ShouldBe("****");
        }

        // The masked payload never carries the actual hash, whichever property it landed on.
        payload.GetRawText().ShouldNotContain(oldPasswordHash, Case.Insensitive);
    }

    #region Helpers

    private async Task<(string UserId, string PasswordHash)> CreateActiveUserAsync(string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            FirstName = "Mask",
            LastName = "Audit",
            Email = email,
            UserName = email.Split('@')[0],
            EmailConfirmed = true,
            IsActive = true
        };
        var result = await userManager.CreateAsync(user, password);
        result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));

        return (user.Id, user.PasswordHash!);
    }

    /// <summary>
    /// Polls the EntityChange feed (audit writes are async — see <see cref="AuditTestHelper"/>) for
    /// the row whose key names this user, and returns its raw payload.
    /// </summary>
    private static async Task<JsonElement> PollForUserEntityChangeAsync(
        HttpClient adminClient, string userId, int attempts = AuditTestHelper.DefaultPollAttempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            var page = await AuditTestHelper.GetAuditsPageAsync(
                adminClient, pageSize: 100, extraQuery: $"eventType={AuditEventType.EntityChange}");

            foreach (var row in page.Items)
            {
                var detail = await AuditTestHelper.GetByIdAsync(adminClient, row.Id);
                if (detail is null)
                {
                    continue;
                }

                // The user's row is touched by more than one write in this flow (e.g. a login resets
                // AccessFailedCount before the password itself changes), so match on the one carrying
                // the property this test cares about rather than the first AppUser/{userId} row seen.
                if (detail.Payload.TryGetProperty("entityName", out var entityName)
                    && entityName.GetString() == "AppUser"
                    && detail.Payload.TryGetProperty("key", out var key)
                    && key.GetString() == $"Id:{userId}"
                    && detail.Payload.TryGetProperty("changes", out var changes)
                    && changes.EnumerateArray().Any(c => c.GetProperty("name").GetString() == "PasswordHash"))
                {
                    return detail.Payload;
                }
            }

            await Task.Delay(AuditTestHelper.PollInterval).ConfigureAwait(false);
        }

        throw new TimeoutException($"No EntityChange audit row for user '{userId}' within {attempts} attempts.");
    }

    #endregion
}
