using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Data;
using Boilerplate.Modules.Multitenancy.Provisioning;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boilerplate.DbMigrator.DemoSeed;

/// <summary>
/// Seeds the demo accounts: the <c>acme</c> and <c>globex</c> tenants, their people, their two
/// custom roles and their groups. Invoked by the migrator's <c>apply --demo</c> flag only — never
/// by the API runtime, and never in Production (see <see cref="DemoSeedGuard"/>).
///
/// A template nobody can sign in to cannot be evaluated; that is the whole justification for this
/// file. A product built from the template deletes it, or replaces <see cref="DemoDataset"/> with
/// its own people.
///
/// <para><b>Tenants come up ready.</b> Each demo tenant is created through
/// <see cref="ITenantService"/> — the same service the runtime's CreateTenant path uses — and then
/// migrated and seeded inline, so "the tenant is ready" is true by the time this returns rather
/// than whenever a background job happens to finish. The tenant admin password is handed over the
/// same way the runtime hands it over, through <see cref="ITenantInitialPasswordBuffer"/>, so
/// <c>admin@acme.com</c> is *created* on the demo password and nothing ever resets a password.</para>
///
/// <para><b>Idempotent.</b> Every step checks before it writes, so a second run changes nothing
/// and fails nothing. Two deliberate limits on what a re-run touches: it never rewrites a
/// password (a developer who changed one keeps it), and it never reactivates a user someone
/// deactivated. It does top up what is missing outright — a hard-deleted user, a role assignment,
/// a group membership — which is how the pre-ADR-0003 seeder behaved and what makes "drop the
/// database and run it again" work.</para>
/// </summary>
internal sealed class DemoSeeder
{
    /// <summary>Audit stamp on every row this seeder writes, so demo data is identifiable.</summary>
    private const string SeededBy = "DemoSeeder";

    private readonly IServiceProvider _services;
    private readonly ILogger<DemoSeeder> _logger;

    public DemoSeeder(IServiceProvider services, ILogger<DemoSeeder> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Creates (or tops up) every tenant in <see cref="DemoDataset.Tenants"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The host is Production, the demo password is missing or unusable, or a demo user could not
    /// be created — all of which must fail the migrator rather than leave a half-seeded stack.
    /// </exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        DemoSeedGuard.EnsureEnvironmentAllowsDemoSeeding(_services.GetRequiredService<IHostEnvironment>());

        var password = DemoSeedGuard.ResolveDemoPassword(
            _services.GetRequiredService<IConfiguration>(),
            _services.GetRequiredService<IOptions<IdentityOptions>>().Value.Password);

        foreach (var demo in DemoDataset.Tenants)
        {
            await EnsureTenantAsync(demo, password, cancellationToken).ConfigureAwait(false);
            await SeedTenantContentAsync(demo, password, cancellationToken).ConfigureAwait(false);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[demo-seed] complete · {Tenants} seeded with demo accounts",
                string.Join(", ", DemoDataset.Tenants.Select(t => t.Id)));
        }
    }

    // ─── Tenant provisioning ────────────────────────────────────────────

    /// <summary>
    /// Puts the tenant in the catalog if it is missing, then walks it through the same
    /// migrate + seed path <c>ITenantService</c> gives the runtime. Provisioning is driven
    /// inline instead of through <c>ITenantProvisioningService</c>: the migrator is a one-shot
    /// process with no job server, and a demo tenant that is "ready when the job gets to it"
    /// is a tenant the developer cannot sign in to when the command returns.
    /// </summary>
    private async Task EnsureTenantAsync(DemoTenant demo, string password, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var provider = scope.ServiceProvider;

        var tenantStore = provider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenantService = provider.GetRequiredService<ITenantService>();

        // Buffer the admin password BEFORE the seed step, exactly as CreateTenantCommandHandler
        // does: IdentityDbInitializer consumes it when it creates admin@<tenant>. On a re-run the
        // admin already exists, nothing consumes it, and the buffer dies with the process.
        provider.GetRequiredService<ITenantInitialPasswordBuffer>().Store(demo.Id, password);

        var tenant = await tenantStore.GetAsync(demo.Id).ConfigureAwait(false);
        if (tenant is null)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("[demo-seed] creating tenant '{TenantId}'", demo.Id);
            }

            await tenantService.CreateAsync(
                demo.Id,
                demo.Name,
                connectionString: null,
                demo.AdminEmail,
                demo.Issuer,
                validUpto: TimeProvider.System.GetUtcNow().UtcDateTime.AddYears(1),
                cancellationToken).ConfigureAwait(false);

            tenant = await tenantStore.GetAsync(demo.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Tenant '{demo.Id}' was created but is not in the store — demo seeding cannot continue.");
        }

        await tenantService.MigrateTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
        await tenantService.SeedTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await EnsureProvisioningRecordAsync(
            provider.GetRequiredService<TenantDbContext>(), demo.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Demo tenants are migrated and seeded inline above, bypassing the provisioning pipeline — so
    /// no <see cref="TenantProvisioning"/> row exists and the console's Provisioning panel would
    /// 404. Record a completed run so the panel shows the history it would show for any other
    /// tenant. Idempotent: skips when the tenant already has one.
    /// </summary>
    private static async Task EnsureProvisioningRecordAsync(
        TenantDbContext tenantDb, string tenantId, CancellationToken cancellationToken)
    {
        var alreadyTracked = await tenantDb.Set<TenantProvisioning>()
            .AnyAsync(p => p.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyTracked)
        {
            return;
        }

        var provisioning = new TenantProvisioning(tenantId, Guid.NewGuid().ToString());
        foreach (var stepName in Enum.GetValues<TenantProvisioningStepName>())
        {
            var step = new TenantProvisioningStep(provisioning.Id, stepName);
            step.MarkRunning();
            step.MarkCompleted();
            provisioning.Steps.Add(step);
        }

        provisioning.MarkCompleted();

        tenantDb.Add(provisioning);
        await tenantDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── People, roles and groups ───────────────────────────────────────

    /// <summary>
    /// Everything inside one tenant, run through <see cref="ITenantScope"/> — the only way to
    /// enter a tenant outside a request (ADR-0002). The scope is opened once and the three steps
    /// share it, because each needs the same tenant-scoped <c>IdentityDbContext</c>.
    /// </summary>
    private async Task SeedTenantContentAsync(DemoTenant demo, string password, CancellationToken cancellationToken)
    {
        var tenantScope = _services.GetRequiredService<ITenantScope>();

        await tenantScope.RunAsync(demo.Id, async (provider, ct) =>
        {
            var roleManager = provider.GetRequiredService<RoleManager<AppRole>>();
            var userManager = provider.GetRequiredService<UserManager<AppUser>>();
            var context = provider.GetRequiredService<IdentityDbContext>();

            await SeedCustomRolesAsync(demo, roleManager, context, ct).ConfigureAwait(false);
            await SeedUsersAsync(demo, password, userManager, roleManager, context, ct).ConfigureAwait(false);
            await SeedGroupsAsync(demo, userManager, context, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedCustomRolesAsync(
        DemoTenant demo,
        RoleManager<AppRole> roleManager,
        IdentityDbContext context,
        CancellationToken cancellationToken)
    {
        foreach (var demoRole in DemoDataset.DemoRoles.All)
        {
            var role = await roleManager.FindByNameAsync(demoRole.Name).ConfigureAwait(false);
            if (role is null)
            {
                role = new AppRole(demoRole.Name, demoRole.Description);
                var created = await roleManager.CreateAsync(role).ConfigureAwait(false);
                if (!created.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"[{demo.Id}] failed to create demo role '{demoRole.Name}': "
                        + string.Join("; ", created.Errors.Select(e => e.Description)));
                }

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[demo-seed] [{Tenant}] created role '{Role}'", demo.Id, demoRole.Name);
                }
            }

            // Written through the DbContext rather than RoleManager.AddClaimAsync so the audit
            // columns on AppRoleClaim are filled in, the way IdentityDbInitializer fills them.
            var existingClaims = await roleManager.GetClaimsAsync(role).ConfigureAwait(false);
            var added = 0;
            foreach (var permission in demoRole.Permissions)
            {
                if (existingClaims.Any(c => c.Type == ClaimConstants.Permission && c.Value == permission))
                {
                    continue;
                }

                context.RoleClaims.Add(new AppRoleClaim
                {
                    RoleId = role.Id,
                    ClaimType = ClaimConstants.Permission,
                    ClaimValue = permission,
                    CreatedBy = SeededBy,
                    CreatedOn = TimeProvider.System.GetUtcNow(),
                });
                added++;
            }

            if (added > 0)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SeedUsersAsync(
        DemoTenant demo,
        string password,
        UserManager<AppUser> userManager,
        RoleManager<AppRole> roleManager,
        IdentityDbContext context,
        CancellationToken cancellationToken)
    {
        foreach (var demoUser in demo.Users)
        {
            var user = await userManager.FindByEmailAsync(demoUser.Email).ConfigureAwait(false);
            if (user is null)
            {
                user = new AppUser
                {
                    UserName = demoUser.UserName,
                    Email = demoUser.Email,
                    FirstName = demoUser.FirstName,
                    LastName = demoUser.LastName,
                    // Confirmed and active so the account can sign in the moment the migrator
                    // exits — there is no mailbox to open a confirmation link from.
                    EmailConfirmed = true,
                    PhoneNumberConfirmed = true,
                    IsActive = true,
                };

                var created = await userManager.CreateAsync(user, password).ConfigureAwait(false);
                if (!created.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"[{demo.Id}] failed to create demo user '{demoUser.Email}': "
                        + string.Join("; ", created.Errors.Select(e => e.Description)));
                }

                await AddToDefaultGroupsAsync(user, context, cancellationToken).ConfigureAwait(false);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[demo-seed] [{Tenant}] created user '{Email}'", demo.Id, demoUser.Email);
                }
            }

            // Role assignment is topped up even for an existing user: a role added to the dataset
            // reaches accounts that already exist, which is the only kind of drift worth fixing.
            if (await roleManager.FindByNameAsync(demoUser.Role).ConfigureAwait(false) is not null
                && !await userManager.IsInRoleAsync(user, demoUser.Role).ConfigureAwait(false))
            {
                await userManager.AddToRoleAsync(user, demoUser.Role).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Mirrors what registration does for a real sign-up: every new user joins the tenant's
    /// default groups ("All Users"), so demo accounts are not a special shape of user.
    /// </summary>
    private static async Task AddToDefaultGroupsAsync(
        AppUser user, IdentityDbContext context, CancellationToken cancellationToken)
    {
        var defaultGroups = await context.Groups
            .AsNoTracking()
            .Where(g => g.IsDefault && !g.IsDeleted)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var groupId in defaultGroups)
        {
            context.UserGroups.Add(UserGroup.Create(user.Id, groupId, SeededBy));
        }

        if (defaultGroups.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SeedGroupsAsync(
        DemoTenant demo,
        UserManager<AppUser> userManager,
        IdentityDbContext context,
        CancellationToken cancellationToken)
    {
        foreach (var demoGroup in demo.Groups)
        {
            var group = await context.Groups
                .FirstOrDefaultAsync(g => g.Name == demoGroup.Name && !g.IsDeleted, cancellationToken)
                .ConfigureAwait(false);

            if (group is null)
            {
                group = Group.Create(
                    name: demoGroup.Name,
                    description: demoGroup.Description,
                    isDefault: false,
                    isSystemGroup: false,
                    createdBy: SeededBy);

                context.Groups.Add(group);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[demo-seed] [{Tenant}] created group '{Group}'", demo.Id, demoGroup.Name);
                }
            }

            var added = 0;
            foreach (var email in demoGroup.MemberEmails)
            {
                var member = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
                if (member is null)
                {
                    continue;
                }

                var alreadyMember = await context.UserGroups
                    .AnyAsync(ug => ug.GroupId == group.Id && ug.UserId == member.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (alreadyMember)
                {
                    continue;
                }

                context.UserGroups.Add(UserGroup.Create(member.Id, group.Id, SeededBy));
                added++;
            }

            if (added > 0)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
