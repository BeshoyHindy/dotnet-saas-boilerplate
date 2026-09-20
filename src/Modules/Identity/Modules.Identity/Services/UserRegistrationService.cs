using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Jobs.Services;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.Events;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace Boilerplate.Modules.Identity.Services;

internal sealed class UserRegistrationService(
    UserManager<AppUser> userManager,
    IdentityDbContext db,
    IJobService jobService,
    IMailService mailService,
    ConfirmationMailBuilder confirmationMailBuilder,
    IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
    IOutboxWriter outbox) : IUserRegistrationService
{
    /// <summary>
    /// The one message a caller who lost a registration race is told, whatever they lost it to.
    /// Identical to what the pre-insert duplicate check produces, so a race is indistinguishable
    /// from a sequential duplicate — which is what it is.
    /// </summary>
    private const string RegistrationFailedMessage = "Unable to register the user.";

    /// <summary>What an external create is refused with when the username is not the caller's to take.</summary>
    private const string ExternalCreateFailedMessage = "Failed to create user from external principal.";

    /// <summary>
    /// How many usernames one external sign-in may try. Each retry draws fresh randomness, so two is
    /// already generous; the bound is here so a name that can never be taken ends as an error rather
    /// than a loop.
    /// </summary>
    private const int MaxUserNameAttempts = 3;

    public async Task<string> GetOrCreateFromPrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();
        ArgumentNullException.ThrowIfNull(principal);

        var email = ExtractEmailFromPrincipal(principal);

        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            return existingUser.Id;
        }

        // That check can be true again a millisecond later, so losing the create is a normal outcome
        // here rather than an error: the user this call was asked to get-or-create exists, and the
        // caller wanted their id. A username collision is the opposite — the row belongs to a
        // stranger who derived the same name — so it is retried under another name, never adopted.
        string? retryUserName = null;

        for (var attempt = 1; attempt <= MaxUserNameAttempts; attempt++)
        {
            try
            {
                return await InTransactionAsync(async ct =>
                {
                    var user = await CreateUserFromPrincipalAsync(principal, email, retryUserName);
                    await AssignDefaultRoleAndGroupsAsync(user, "ExternalAuth", ct);
                    await PublishUserRegisteredAsync(user, "Identity.ExternalAuth", ct);

                    return user.Id;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (RegistrationConflict.Describe(ex).IsDuplicate)
            {
                // Whichever shape refused this attempt — the unique index (23505, a DbUpdateException)
                // or Identity's own pre-insert validators a moment after the winner committed
                // (DuplicateEmail/DuplicateUserName, no exception of its own) — the question is the
                // same: does this address have a user now? The change tracker was cleared on the way
                // out of the transaction, so this reads committed state.
                var winner = await userManager.FindByEmailAsync(email);
                if (winner is not null)
                {
                    return winner.Id;
                }

                // The address is still free, so nothing raced this identity: someone else holds the
                // username. Retrying is the only answer that neither fails a valid sign-in nor hands
                // the caller a stranger's account.
                if (!RegistrationConflict.Describe(ex).UserNameTaken || attempt >= MaxUserNameAttempts)
                {
                    throw;
                }

                retryUserName = UniqueUserNameFor(ExtractUserInfoFromPrincipal(principal, email).userName);
            }
        }

        // The loop either returns, or rethrows on its last attempt.
        throw new UnreachableException();
    }

    public async Task<string> RegisterAsync(
        string firstName,
        string lastName,
        string email,
        string userName,
        string password,
        string confirmPassword,
        string phoneNumber,
        CancellationToken cancellationToken)
    {
        EnsureValidTenant();
        ValidatePasswordMatch(password, confirmPassword);

        // The confirmation mail is NOT sent here. It hangs off UserRegisteredIntegrationEvent
        // (UserRegisteredConfirmationMailHandler), whose outbox row commits with the rows below —
        // so a registration that rolls back cannot have mailed a link to an account that does not
        // exist, and one that commits cannot lose the mail.
        try
        {
            return await InTransactionAsync(async ct =>
            {
                var user = await CreateUserWithPasswordAsync(firstName, lastName, email, userName, password, phoneNumber);
                await AssignDefaultRoleAndGroupsAsync(user, "System", ct);
                await PublishUserRegisteredAsync(user, "Identity", ct);

                return user.Id;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (RegistrationConflict.IsDuplicateUser(ex))
        {
            // A concurrent registration got there first. Identity's pre-insert duplicate check lost
            // the race to the index, so say what that check would have said: this is the caller's
            // duplicate (400), not a server fault (500).
            throw new CustomException(
                RegistrationFailedMessage,
                [RegistrationConflict.DuplicateReasonFor(ex, email, userName)],
                HttpStatusCode.BadRequest);
        }
    }

    public async Task<string> ConfirmEmailAsync(string userId, string code, string tenant, CancellationToken cancellationToken)
    {
        EnsureValidTenant();

        var user = await userManager.Users
            .Where(u => u.Id == userId && !u.EmailConfirmed)
            .FirstOrDefaultAsync(cancellationToken);

        _ = user ?? throw new CustomException("An error occurred while confirming E-Mail.");

        code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        var result = await userManager.ConfirmEmailAsync(user, code);

        return result.Succeeded
            ? string.Format(CultureInfo.InvariantCulture, "Account Confirmed for E-Mail {0}. You can now use the /api/tokens endpoint to generate JWT.", user.Email)
            : throw new CustomException(string.Format(CultureInfo.InvariantCulture, "An error occurred while confirming {0}", user.Email));
    }

    public async Task AdminConfirmEmailAsync(string userId, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var user = await userManager.Users
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} was not found.");

        // Idempotent: a second confirm is a no-op rather than an error.
        if (user.EmailConfirmed)
        {
            return;
        }

        user.EmailConfirmed = true;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new CustomException(string.Format(
                CultureInfo.InvariantCulture,
                "An error occurred while confirming the email for {0}: {1}",
                user.Email,
                string.Join("; ", result.Errors.Select(e => e.Description))));
        }
    }

    public async Task ResendConfirmationEmailAsync(string userId, string origin, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var user = await userManager.Users
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} was not found.");

        if (user.EmailConfirmed)
        {
            throw new CustomException(string.Format(
                CultureInfo.InvariantCulture,
                "The email for {0} is already confirmed.",
                user.Email));
        }

        await SendConfirmationEmailAsync(user, origin, cancellationToken);
    }

    public async Task<string> ConfirmPhoneNumberAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var user = await userManager.Users
            .Where(u => u.Id == userId && !u.PhoneNumberConfirmed)
            .FirstOrDefaultAsync(cancellationToken);

        _ = user ?? throw new CustomException("An error occurred while confirming phone number.");

        code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        var result = await userManager.ChangePhoneNumberAsync(user, user.PhoneNumber!, code);

        return result.Succeeded
            ? string.Format(CultureInfo.InvariantCulture, "Phone number {0} confirmed successfully.", user.PhoneNumber)
            : throw new CustomException(string.Format(CultureInfo.InvariantCulture, "An error occurred while confirming phone number {0}", user.PhoneNumber));
    }

    /// <summary>
    /// Runs the whole of a registration in one database transaction: the user row, the role, the
    /// default groups and the outbox row either all exist or none of them do (#86). Before this,
    /// each was its own commit, and a failure after the first left a user with no role, no groups
    /// and no event — a row every retry was then refused on, with no way for the caller to recover.
    ///
    /// <para><see cref="UserManager{T}"/> writes through this same scoped <see cref="IdentityDbContext"/>,
    /// and the outbox joins the ambient transaction through the shared scope connection
    /// (<c>.agents/rules/eventing.md</c> §Atomicity), so one <c>BeginTransaction</c> here covers
    /// every write registration makes.</para>
    ///
    /// <para>The body runs through the provider's execution strategy. Postgres is configured without
    /// retry-on-failure today, so it executes exactly once; wrapping it anyway is what keeps
    /// enabling retries a configuration change rather than a crash (a user-initiated transaction
    /// under a retrying strategy throws). The change tracker is cleared at the top of each attempt
    /// so a retry starts from committed state instead of the failed attempt's leftovers, and again
    /// on the way out of a failed one — the rows were rolled back, so a context still tracking them
    /// would re-insert them on the next save anything in this scope makes.</para>
    /// </summary>
    private async Task<string> InTransactionAsync(
        Func<CancellationToken, Task<string>> register,
        CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            cancellationToken,
            async (ct) =>
            {
                db.ChangeTracker.Clear();

                try
                {
                    // Disposal rolls back anything not committed, which is the whole point: every exit
                    // that is not the commit below leaves the database as it found it.
                    await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

                    var userId = await register(ct).ConfigureAwait(false);

                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return userId;
                }
                catch
                {
                    // One place for every failed exit — the caller that maps a duplicate to 400, the
                    // caller that re-finds the winner, and the failure nobody catches all leave the
                    // scoped context as clean as the database.
                    db.ChangeTracker.Clear();
                    throw;
                }
            }).ConfigureAwait(false);
    }

    private void EnsureValidTenant()
    {
        if (string.IsNullOrWhiteSpace(multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id))
        {
            throw new UnauthorizedException("invalid tenant");
        }
    }

    private static string ExtractEmailFromPrincipal(ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? throw new CustomException("Email claim is required for external authentication.");
    }

    /// <param name="principal">The externally authenticated caller.</param>
    /// <param name="email">The address the provider vouches for.</param>
    /// <param name="userNameOverride">
    /// A username to use instead of the derived one — set by a retry whose derived name turned out to
    /// belong to someone else.
    /// </param>
    private async Task<AppUser> CreateUserFromPrincipalAsync(
        ClaimsPrincipal principal,
        string email,
        string? userNameOverride = null)
    {
        var (firstName, lastName, userName) = ExtractUserInfoFromPrincipal(principal, email);

        userName = userNameOverride ?? await EnsureUniqueUserNameAsync(userName);

        var user = new AppUser
        {
            Email = email,
            UserName = userName,
            FirstName = firstName,
            LastName = lastName,
            EmailConfirmed = true,
            PhoneNumberConfirmed = false,
            IsActive = true
        };

        var result = await userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            // A duplicate carries which field collided, because the caller recovers from an address
            // collision and must not recover from a username one. It is still the same 400, with the
            // same message and the same reasons, for anyone who just lets it escape.
            throw RegistrationConflict.Describe(result).IsDuplicate
                ? new DuplicateUserException(ExternalCreateFailedMessage, result)
                : new CustomException(
                    ExternalCreateFailedMessage,
                    result.Errors.Select(e => e.Description).ToList(),
                    HttpStatusCode.BadRequest);
        }

        return user;
    }

    private static (string firstName, string lastName, string userName) ExtractUserInfoFromPrincipal(
        ClaimsPrincipal principal, string email)
    {
        var firstName = principal.FindFirstValue(ClaimTypes.GivenName)
            ?? principal.FindFirstValue("given_name")
            ?? string.Empty;

        var lastName = principal.FindFirstValue(ClaimTypes.Surname)
            ?? principal.FindFirstValue("family_name")
            ?? string.Empty;

        var userName = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("preferred_username")
            ?? email.Split('@')[0];

        return (firstName, lastName, userName);
    }

    private async Task<string> EnsureUniqueUserNameAsync(string userName)
    {
        if (await userManager.FindByNameAsync(userName) is not null)
        {
            return UniqueUserNameFor(userName);
        }
        return userName;
    }

    /// <summary>
    /// A username derived from <paramref name="userName"/> that nobody else is holding.
    ///
    /// The random half is what makes it unique, so the stem is trimmed to fit around it rather than
    /// the other way round. It used to be <c>$"{userName}_{Guid}"[..20]</c>, which for any name of 20
    /// characters or more truncated the randomness clean off and returned a deterministic prefix — so
    /// the second caller to need a fallback got the name the first one had already been given, while
    /// their own address was still free. That is the one collision this path must never produce,
    /// because the name is all that separates two people who happen to share a mailbox local part.
    /// </summary>
    private static string UniqueUserNameFor(string userName)
    {
        const int suffixLength = 8;
        const int maxLength = 20;

        var stem = userName.Length > maxLength - suffixLength - 1
            ? userName[..(maxLength - suffixLength - 1)]
            : userName;

        return $"{stem}_{Guid.NewGuid():N}"[..(stem.Length + 1 + suffixLength)];
    }

    private static void ValidatePasswordMatch(string password, string confirmPassword)
    {
        if (password != confirmPassword)
        {
            throw new CustomException(
                "Passwords do not match.",
                errors: null,
                HttpStatusCode.BadRequest);
        }
    }

    private async Task<AppUser> CreateUserWithPasswordAsync(
        string firstName,
        string lastName,
        string email,
        string userName,
        string password,
        string phoneNumber)
    {
        var user = new AppUser
        {
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            UserName = userName,
            PhoneNumber = phoneNumber,
            IsActive = true,
            EmailConfirmed = false,
            PhoneNumberConfirmed = false,
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            // Identity create failures (duplicate email/username, password policy, …) are
            // client-input errors, not server faults — surface them as 400 with the specific
            // reasons so the caller sees *why* registration failed, not a bare 500.
            var errors = result.Errors.Select(error => error.Description).ToList();
            throw new CustomException(
                RegistrationFailedMessage,
                errors,
                HttpStatusCode.BadRequest);
        }

        return user;
    }

    private async Task AssignDefaultRoleAndGroupsAsync(
        AppUser user,
        string source,
        CancellationToken cancellationToken = default)
    {
        await userManager.AddToRoleAsync(user, RoleConstants.Basic);

        var defaultGroups = await db.Groups
            .AsNoTracking()
            .Where(g => g.IsDefault && !g.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var group in defaultGroups)
        {
            db.UserGroups.Add(UserGroup.Create(user.Id, group.Id, source));
        }

        if (defaultGroups.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The resend path only. A first registration's confirmation mail is sent from the event
    /// (<c>UserRegisteredConfirmationMailHandler</c>) so it cannot outlive a rolled-back sign-up;
    /// a resend has a committed user by definition, so queueing it keeps the request short.
    /// </summary>
    private async Task SendConfirmationEmailAsync(AppUser user, string origin, CancellationToken cancellationToken)
    {
        var mailRequest = await confirmationMailBuilder.BuildAsync(user, origin).ConfigureAwait(false);
        if (mailRequest is null)
        {
            return;
        }

        jobService.Enqueue("email", () => mailService.SendAsync(mailRequest, cancellationToken));
    }

    private async Task PublishUserRegisteredAsync(
        AppUser user,
        string source,
        CancellationToken cancellationToken = default)
    {
        var tenantId = multiTenantContextAccessor.MultiTenantContext.TenantInfo?.Id;
        user.RecordRegistered(tenantId);

        await db.SaveChangesAsync(cancellationToken);

        var integrationEvent = new UserRegisteredIntegrationEvent(
            Id: Guid.NewGuid(),
            OccurredOnUtc: TimeProvider.System.GetUtcNow().UtcDateTime,
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid().ToString(),
            Source: source,
            UserId: user.Id,
            Email: user.Email ?? string.Empty,
            FirstName: user.FirstName ?? string.Empty,
            LastName: user.LastName ?? string.Empty);

        await outbox.AddAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
    }
}
