using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Data;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// Registration is one fact about the world — a user exists, with their role, their default groups
/// and the event that says so — and #86 is the observation that it was four independent commits.
/// A failure after the first left a user row nothing could finish and no retry could get past, and
/// e-mail uniqueness lived only in a validator's query, so two concurrent sign-ups with the same
/// address both won.
///
/// These tests assert the two halves of the fix at the seam a caller actually has: the HTTP
/// endpoint, plus the rows and the mail it leaves behind. The rollback case is driven by
/// <see cref="FaultInjectingOutboxStore"/>, which lets the outbox row be written and then throws —
/// so "no outbox row survives" is a statement about the transaction the row joined, not about a
/// write that never happened.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class RegistrationAtomicityTests
{
    /// <summary>How many sign-ups race. Enough that at least one pair overlaps in the database.</summary>
    private const int Racers = 8;

    /// <summary>Subject of the mail registration owes a self-registered user.</summary>
    private const string ConfirmationSubject = "Confirm Your Email Address";

    /// <summary>
    /// How long a mail that must NOT arrive is given to arrive anyway. The pre-fix path enqueued it
    /// on Hangfire's "email" queue (1s polling), so a bare assertion would pass by being too quick.
    /// </summary>
    private static readonly TimeSpan MailGracePeriod = TimeSpan.FromSeconds(5);

    private readonly AppWebApplicationFactory _factory;

    public RegistrationAtomicityTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    #region Partial failure

    [Fact]
    public async Task Register_Should_LeaveNoTrace_And_AcceptAnIdenticalRetry_When_AStepAfterUserCreationFails()
    {
        // Arrange
        var mail = (NoOpMailService)_factory.Services.GetRequiredService<IMailService>();
        mail.Clear();
        await OutboxDrain.DrainAsync(_factory.Services);

        using var client = _factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"{FaultInjectingOutboxStore.EmailPrefix}{unique}@example.com";
        var payload = NewRegistration(email, $"regfail{unique}");

        // Act — fail the publish, which is the last step of registration: user, role, groups and the
        // outbox row have all been written by the time it throws.
        HttpResponseMessage failed;
        FaultInjectingOutboxStore.Arm(email);
        try
        {
            failed = await client.PostAsJsonAsync($"{TestConstants.RootAuthBasePath}/register", payload);
        }
        finally
        {
            FaultInjectingOutboxStore.Disarm(email);
        }

        // Assert — the attempt failed and took everything it had written with it.
        failed.IsSuccessStatusCode.ShouldBeFalse("the injected fault must not be swallowed");

        (await CountUsersAsync(TestConstants.RootTenantId, email))
            .ShouldBe(0, "a registration that did not finish must leave no user row to be wedged on");
        (await CountRoleRowsForAsync(TestConstants.RootTenantId, email))
            .ShouldBe(0, "the Basic role assignment belongs to the same fact as the user row");
        (await CountGroupRowsForAsync(TestConstants.RootTenantId, email))
            .ShouldBe(0, "so do the default-group memberships");
        (await CountOutboxRowsMentioningAsync(email))
            .ShouldBe(0, "the outbox row joins the registration transaction (eventing.md §Atomicity)");

        // Nothing rolled back may be announced by e-mail either. Drain first, then wait out the
        // Hangfire queue, so a mail that was going to arrive has every chance to.
        await OutboxDrain.DrainAsync(_factory.Services);
        await Task.Delay(MailGracePeriod);
        CountMailsTo(mail, email, ConfirmationSubject)
            .ShouldBe(0, "a confirmation for an account that does not exist is a dead link");

        // Act — the same payload again, with nothing armed.
        var retry = await client.PostAsJsonAsync($"{TestConstants.RootAuthBasePath}/register", payload);

        // Assert — self-service recovery: the retry is a first registration, not a duplicate.
        retry.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"an identical retry must succeed: {await retry.Content.ReadAsStringAsync()}");

        var registered = await retry.DeserializeAsync<RegisterResult>();
        registered.UserId.ShouldNotBeNullOrWhiteSpace();
        (await CountUsersAsync(TestConstants.RootTenantId, email)).ShouldBe(1);
        (await CountRoleRowsForAsync(TestConstants.RootTenantId, email))
            .ShouldBe(1, "the retry must build a complete user, not repair a half-built one");
    }

    #endregion

    #region Concurrent duplicates

    [Fact]
    public async Task Register_Should_CreateExactlyOneUser_When_TheSameEmailRacesWithDifferentUserNames()
    {
        using var client = _factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"race-{unique}@example.com";

        var responses = await Task.WhenAll(Enumerable.Range(0, Racers).Select(i =>
            client.PostAsJsonAsync(
                $"{TestConstants.RootAuthBasePath}/register",
                NewRegistration(email, $"race{i}{unique}"))));

        await AssertExactlyOneWinnerAsync(responses);
        (await CountUsersAsync(TestConstants.RootTenantId, email))
            .ShouldBe(1, "e-mail uniqueness must be the database's answer, not a validator's read");
    }

    [Fact]
    public async Task Register_Should_CreateExactlyOneUser_When_TheSameUserNameRacesWithDifferentEmails()
    {
        using var client = _factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var userName = $"racename{unique}";

        var responses = await Task.WhenAll(Enumerable.Range(0, Racers).Select(i =>
            client.PostAsJsonAsync(
                $"{TestConstants.RootAuthBasePath}/register",
                NewRegistration($"racename-{i}-{unique}@example.com", userName))));

        await AssertExactlyOneWinnerAsync(responses);
        (await CountUsersByUserNameAsync(TestConstants.RootTenantId, userName))
            .ShouldBe(1, "the username index has always been unique; the loser just has to hear 400");
    }

    [Fact]
    public async Task Register_Should_Succeed_InBothTenants_When_TheSameEmailIsUsedInAnother()
    {
        // Arrange — a second, really provisioned tenant, made the way tenants are made.
        var (otherTenantId, _) = await new TenantFixtures(_factory).CreateProvisionedTenantAsync("emailscope");

        using var client = _factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"shared-{unique}@example.com";

        // Act
        var inRoot = await client.PostAsJsonAsync(
            $"{TestConstants.RootAuthBasePath}/register",
            NewRegistration(email, $"sharedroot{unique}"));
        var inOther = await client.PostAsJsonAsync(
            $"{TestConstants.AuthBasePath(otherTenantId)}/register",
            NewRegistration(email, $"sharedother{unique}"));

        // Assert — the uniqueness boundary is the tenant, like every other row (ADR-0002).
        inRoot.StatusCode.ShouldBe(HttpStatusCode.Created, await inRoot.Content.ReadAsStringAsync());
        inOther.StatusCode.ShouldBe(HttpStatusCode.Created, await inOther.Content.ReadAsStringAsync());

        (await CountUsersAsync(TestConstants.RootTenantId, email)).ShouldBe(1);
        (await CountUsersAsync(otherTenantId, email)).ShouldBe(1);
    }

    #endregion

    #region Confirmation mail

    [Fact]
    public async Task Register_Should_SendOneConfirmationMail_When_TheRegistrationCommits()
    {
        // Arrange
        var mail = (NoOpMailService)_factory.Services.GetRequiredService<IMailService>();
        mail.Clear();

        using var client = _factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"confirmed-{unique}@example.com";

        // Act
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.RootAuthBasePath}/register",
            NewRegistration(email, $"confirmed{unique}"));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        // The mail now rides the event, so it arrives on a dispatch cycle rather than in the request.
        await OutboxDrain.DrainAsync(_factory.Services);
        await Task.Delay(MailGracePeriod);

        // Assert
        CountMailsTo(mail, email, ConfirmationSubject).ShouldBe(
            1,
            "a committed registration owes the user exactly one confirmation link");
    }

    #endregion

    #region Helpers

    private static object NewRegistration(string email, string userName) => new
    {
        firstName = "Atomic",
        lastName = "Register",
        email,
        userName,
        password = TestConstants.DefaultPassword,
        confirmPassword = TestConstants.DefaultPassword,
    };

    private static async Task AssertExactlyOneWinnerAsync(HttpResponseMessage[] responses)
    {
        var codes = responses.Select(r => r.StatusCode).ToArray();

        var faults = new List<string>();
        foreach (var response in responses.Where(r =>
            r.StatusCode != HttpStatusCode.Created && r.StatusCode != HttpStatusCode.BadRequest))
        {
            faults.Add($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        faults.ShouldBeEmpty("a lost race is the caller's duplicate, not a server fault — 400, never 500");
        codes.Count(c => c == HttpStatusCode.Created).ShouldBe(
            1,
            $"exactly one racer may win; got [{string.Join(", ", codes.Select(c => (int)c))}]");
    }

    private async Task<int> CountUsersAsync(string tenantId, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(tenantId);
        // Inline, in the method that queries: Finbuckle's context is an AsyncLocal that does not
        // flow back out of an awaited helper, and the tenant filter NREs without it.
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return await db.Users.AsNoTracking().CountAsync(u => u.Email == email);
    }

    private async Task<int> CountUsersByUserNameAsync(string tenantId, string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(tenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return await db.Users.AsNoTracking().CountAsync(u => u.UserName == userName);
    }

    /// <summary>
    /// Role rows belonging to the user with this e-mail. A foreign key already ties them to the user
    /// row, so this is the same question asked from the other end — it fails loudly if a future fix
    /// ever deletes the user and leaves the grants.
    /// </summary>
    private async Task<int> CountRoleRowsForAsync(string tenantId, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(tenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return await db.UserRoles.AsNoTracking()
            .CountAsync(ur => db.Users.Any(u => u.Id == ur.UserId && u.Email == email));
    }

    private async Task<int> CountGroupRowsForAsync(string tenantId, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(tenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return await db.UserGroups.AsNoTracking()
            .CountAsync(ug => db.Users.Any(u => u.Id == ug.UserId && u.Email == email));
    }

    /// <summary>
    /// Outbox rows whose payload carries this address — the event is serialized, so the address is
    /// the only handle a test has on it before the row is dispatched.
    /// </summary>
    private async Task<int> CountOutboxRowsMentioningAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        return await eventing.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Payload.Contains(email));
    }

    private static int CountMailsTo(NoOpMailService mail, string email, string subject) =>
        mail.Sent.Count(m => m.To.Contains(email) && m.Subject == subject);

    #endregion
}
