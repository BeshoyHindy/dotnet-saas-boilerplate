using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Data;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using System.Security.Claims;

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

    /// <summary>Subject of the mail every registration's integration event sends, self-served or not.</summary>
    private const string WelcomeSubject = "Welcome!";

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
        (await CountOutboxRowsMentioningAsync(email)).ShouldBe(
            1,
            "one sign-up is one UserRegisteredIntegrationEvent — a second publisher means a second of every mail hung off it");
        CountMailsTo(mail, email, ConfirmationSubject).ShouldBe(
            1,
            "a committed registration owes the user exactly one confirmation link");
        CountMailsTo(mail, email, WelcomeSubject).ShouldBe(
            1,
            "and exactly one welcome");
    }

    [Fact]
    public async Task GetOrCreateFromPrincipal_Should_PublishOneEvent_And_SendNoConfirmation()
    {
        // Arrange — an externally authenticated user arrives with their address already proven, so
        // the confirmation link would be a link to nothing they need.
        var mail = (NoOpMailService)_factory.Services.GetRequiredService<IMailService>();
        mail.Clear();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"extmail-{unique}@example.com";

        // Act
        var userId = await SignInExternallyAsync(email);
        userId.ShouldNotBeNullOrWhiteSpace();

        await OutboxDrain.DrainAsync(_factory.Services);
        await Task.Delay(MailGracePeriod);

        // Assert
        (await CountOutboxRowsMentioningAsync(email)).ShouldBe(1, "one sign-up, one event");
        CountMailsTo(mail, email, WelcomeSubject).ShouldBe(1);
        CountMailsTo(mail, email, ConfirmationSubject).ShouldBe(
            0,
            "the address arrived confirmed; a confirmation link would ask the user to prove it twice");
    }

    #endregion

    #region External sign-in

    [Fact]
    public async Task GetOrCreateFromPrincipal_Should_ReturnOneUser_When_TheSameIdentitySignsInConcurrently()
    {
        // A first sign-in through an external provider is a registration, and N devices (or N retries
        // of one flaky callback) can make it at once. Losing that race is not an error — the user the
        // call was asked to get-or-create exists — so every racer must come back with the winner's id
        // rather than a failed login.
        //
        // The gate pins the losing interleaving instead of hoping for it: every racer but the first
        // is held after its "does this address exist?" query came back empty, and released once the
        // winner has committed. That is the window where the index is not what refuses the loser —
        // Identity's pre-insert validators are, with a DuplicateEmail result the caller never sees
        // as a DbUpdateException.
        using var gate = new ExternalSignInGate();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"external-{unique}@example.com";

        var racers = Enumerable.Range(0, Racers)
            .Select(_ => Task.Run(() => SignInExternallyAsync(email, gate.PrincipalFor(email))))
            .ToArray();

        // The unheld racer is the winner; everyone else is parked at the gate until it has committed.
        var winner = await Task.WhenAny(racers);
        await winner;
        gate.ReleaseHeld();

        var ids = await Task.WhenAll(racers);

        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1,
            $"every racer must be handed the same user; got [{string.Join(", ", ids.Distinct(StringComparer.Ordinal))}]");
        ids.ShouldAllBe(id => !string.IsNullOrWhiteSpace(id));
        (await CountUsersAsync(TestConstants.RootTenantId, email)).ShouldBe(
            1,
            "and the tenant must hold exactly one row for that address");
    }

    [Fact]
    public async Task GetOrCreateFromPrincipal_Should_CreateSeparateUsers_When_OtherAddressesDeriveTheSameUserName()
    {
        // A username taken by SOMEONE ELSE is not this identity's race, and must never be answered
        // with that someone else's id — it would sign the caller into a stranger's account. Three
        // addresses at three domains share one local part, so all three derive the same username and
        // must still end up as three people.
        //
        // The third one is the case: the derived name is taken, so registration falls back to a
        // uniquified one — and that fallback must actually be unique. It used to be
        // $"{name}_{guid}"[..20], which for a local part this long truncates the random half clean
        // off, handing the third arrival exactly the name the second one already holds while their
        // address is still free.
        var localPart = $"twins-{Guid.NewGuid():N}"[..24];
        var addresses = new[]
        {
            $"{localPart}@first.example.com",
            $"{localPart}@second.example.com",
            $"{localPart}@third.example.com",
        };

        var ids = new List<string>();
        foreach (var address in addresses)
        {
            ids.Add(await SignInExternallyAsync(address));
        }

        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            3,
            "three addresses are three people, whatever their usernames derive to");
        foreach (var address in addresses)
        {
            (await CountUsersAsync(TestConstants.RootTenantId, address)).ShouldBe(1);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Signs in through the external-authentication path — the service seam, because no endpoint maps
    /// it today. The tenant context is installed INLINE in this method: Finbuckle keeps it in an
    /// AsyncLocal, so a set made anywhere else does not reach the call below.
    /// </summary>
    /// <param name="email">The address the provider vouches for.</param>
    /// <param name="principal">
    /// A principal to sign in with — an <see cref="ExternalSignInGate"/> one when the test needs the
    /// race pinned. Omitted, a plain principal is built for the address.
    /// </param>
    private async Task<string> SignInExternallyAsync(string email, ClaimsPrincipal? principal = null)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        // No name claim, so the username is derived from the address — every racer for one address
        // derives the same one, which is the worst case this path has.
        principal ??= new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.GivenName, "Ext"),
                new Claim(ClaimTypes.Surname, "Auth"),
            ],
            authenticationType: "IntegrationTestExternalProvider"));

        return await scope.ServiceProvider
            .GetRequiredService<IUserService>()
            .GetOrCreateFromPrincipalAsync(principal);
    }

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
