using Asp.Versioning;
using Microsoft.OpenApi;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Eventing;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Storage;
using Boilerplate.BuildingBlocks.Storage.Local;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.BuildingBlocks.Web;
using Boilerplate.BuildingBlocks.Web.Modules;
using Boilerplate.Modules.Identity.Authorization;
using Boilerplate.Modules.Identity.Authorization.Jwt;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Boilerplate.Modules.Identity.Features.v1.Groups.AddUsersToGroup;
using Boilerplate.Modules.Identity.Features.v1.Groups.CreateGroup;
using Boilerplate.Modules.Identity.Features.v1.Groups.DeleteGroup;
using Boilerplate.Modules.Identity.Features.v1.Groups.GetGroupById;
using Boilerplate.Modules.Identity.Features.v1.Groups.GetGroupMembers;
using Boilerplate.Modules.Identity.Features.v1.Groups.GetGroups;
using Boilerplate.Modules.Identity.Features.v1.Groups.RemoveUserFromGroup;
using Boilerplate.Modules.Identity.Features.v1.Groups.UpdateGroup;
using Boilerplate.Modules.Identity.Features.v1.Impersonation.EndImpersonation;
using Boilerplate.Modules.Identity.Features.v1.Impersonation.GetImpersonationGrants;
using Boilerplate.Modules.Identity.Features.v1.Impersonation.RevokeImpersonationGrant;
using Boilerplate.Modules.Identity.Features.v1.Impersonation.StartImpersonation;
using Boilerplate.Modules.Identity.Features.v1.Operators.ExchangeOperatorToken;
using Boilerplate.Modules.Identity.Features.v1.Permissions.GetPermissionCatalog;
using Boilerplate.Modules.Identity.Features.v1.Roles;
using Boilerplate.Modules.Identity.Features.v1.Roles.DeleteRole;
using Boilerplate.Modules.Identity.Features.v1.Roles.GetRoleById;
using Boilerplate.Modules.Identity.Features.v1.Roles.GetRoles;
using Boilerplate.Modules.Identity.Features.v1.Roles.GetRoleWithPermissions;
using Boilerplate.Modules.Identity.Features.v1.Roles.UpdateRolePermissions;
using Boilerplate.Modules.Identity.Features.v1.Roles.UpsertRole;
using Boilerplate.Modules.Identity.Features.v1.Sessions.AdminRevokeAllSessions;
using Boilerplate.Modules.Identity.Features.v1.Sessions.AdminRevokeSession;
using Boilerplate.Modules.Identity.Features.v1.Sessions.GetMySessions;
using Boilerplate.Modules.Identity.Features.v1.Sessions.GetTenantSessions;
using Boilerplate.Modules.Identity.Features.v1.Sessions.GetUserSessions;
using Boilerplate.Modules.Identity.Features.v1.Sessions.RevokeAllSessions;
using Boilerplate.Modules.Identity.Features.v1.Sessions.RevokeSession;
using Boilerplate.Modules.Identity.Features.v1.Tokens.EndSession;
using Boilerplate.Modules.Identity.Features.v1.Tokens.RefreshToken;
using Boilerplate.Modules.Identity.Features.v1.Tokens.TokenGeneration;
using Boilerplate.Modules.Identity.Features.v1.TwoFactor.Disable;
using Boilerplate.Modules.Identity.Features.v1.TwoFactor.Enroll;
using Boilerplate.Modules.Identity.Features.v1.TwoFactor.VerifyEnroll;
using Boilerplate.Modules.Identity.Features.v1.Users.AssignUserRoles;
using Boilerplate.Modules.Identity.Features.v1.Users.ChangePassword;
using Boilerplate.Modules.Identity.Features.v1.Users.AdminConfirmEmail;
using Boilerplate.Modules.Identity.Features.v1.Users.ConfirmEmail;
using Boilerplate.Modules.Identity.Features.v1.Users.ResendConfirmationEmail;
using Boilerplate.Modules.Identity.Features.v1.Users.DeleteUser;
using Boilerplate.Modules.Identity.Features.v1.Users.ForgotPassword;
using Boilerplate.Modules.Identity.Features.v1.Users.GetUserById;
using Boilerplate.Modules.Identity.Features.v1.Users.GetUserGroups;
using Boilerplate.Modules.Identity.Features.v1.Users.GetUserPermissions;
using Boilerplate.Modules.Identity.Features.v1.Users.GetUserProfile;
using Boilerplate.Modules.Identity.Features.v1.Users.GetUserRoles;
using Boilerplate.Modules.Identity.Features.v1.Users.GetUsers;
using Boilerplate.Modules.Identity.Features.v1.Users.RegisterUser;
using Boilerplate.Modules.Identity.Features.v1.Users.ResetPassword;
using Boilerplate.Modules.Identity.Features.v1.Users.SearchUsers;
using Boilerplate.Modules.Identity.Features.v1.Users.SelfRegistration;
using Boilerplate.Modules.Identity.Features.v1.Users.SetProfileImage;
using Boilerplate.Modules.Identity.Features.v1.Users.ToggleUserStatus;
using Boilerplate.Modules.Identity.Features.v1.Users.UpdateUser;
using Boilerplate.Modules.Identity.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Boilerplate.Modules.Identity;

public class IdentityModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        services.AddPermissions(Boilerplate.Modules.Identity.Contracts.Authorization.IdentityPermissions.All);
        services.AddScoped<RolePermissionSyncer>();
        services.AddHostedService<RolePermissionSyncHostedService>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PathAwareAuthorizationHandler>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<ICurrentUserService>());
        services.AddScoped<ICurrentUserInitializer>(sp => sp.GetRequiredService<ICurrentUserService>());
        services.AddScoped<IRequestContextService, RequestContextService>();
        services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<IRequestContextService>());
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IImpersonationGrantService, ImpersonationGrantService>();
        // The one place a token is minted for someone else's identity: impersonation and the root
        // operator token exchange both go through it, so grants, revocation and the lifetime
        // ceiling stay unified (ADR-0002).
        services.AddScoped<IImpersonationTokenIssuer, ImpersonationTokenIssuer>();

        // User services - focused single-responsibility services
        services.AddTransient<IUserRegistrationService, UserRegistrationService>();
        services.AddTransient<IUserProfileService, UserProfileService>();
        services.AddTransient<IUserStatusService, UserStatusService>();
        services.AddTransient<IUserRoleService, UserRoleService>();
        services.AddTransient<IUserPasswordService, UserPasswordService>();
        services.AddTransient<IUserPermissionService, UserPermissionService>();
        services.AddTransient<IPermissionChecker>(sp => sp.GetRequiredService<IUserPermissionService>());

        // Facade for backward compatibility
        services.AddTransient<IUserService, UserService>();

        services.AddTransient<IRoleService, RoleService>();
        services.AddHeroStorage(builder.Configuration);
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddHeroDbContext<IdentityDbContext>();
        // Eventing itself is bootstrapped by the host (AddEventingCore) — the outbox is framework
        // infrastructure, not Identity's. Handler registration stays per module.
        services.AddIntegrationEventHandlers(typeof(IdentityModule).Assembly);
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<IdentityDbContext>(
                name: "db:identity",
                failureStatus: HealthStatus.Unhealthy);
        services.AddScoped<IDbInitializer, IdentityDbInitializer>();

        // Configure password policy options
        services.Configure<PasswordPolicyOptions>(builder.Configuration.GetSection("PasswordPolicy"));

        // Tenant validity grace period (shared "TenantValidity" section) — used by the login expiry check.
        services.Configure<TenantGraceOptions>(builder.Configuration.GetSection(TenantGraceOptions.SectionName));

        // Lifetime ceiling for every acting token (operator exchange + impersonation). Validated on
        // start so a misconfigured Default/Max pair fails the host, not the first exchange.
        services.AddOptions<OperatorExchangeOptions>()
            .BindConfiguration(OperatorExchangeOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Register password history service
        services.AddScoped<IPasswordHistoryService, PasswordHistoryService>();

        // Register password expiry service
        services.AddScoped<IPasswordExpiryService, PasswordExpiryService>();

        // Register session service and background cleanup
        services.AddScoped<ISessionService, SessionService>();
        services.AddHostedService<SessionCleanupHostedService>();

        // Register group role service for group-derived permissions
        services.AddScoped<IGroupRoleService, GroupRoleService>();

        services.AddIdentity<AppUser, AppRole>(options =>
        {
            options.Password.RequiredLength = IdentityModuleConstants.PasswordLength;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = true;
            options.User.RequireUniqueEmail = true;

            // Account lockout: 5 consecutive failed logins → 15-minute lockout (applies to new users by default).
            // IdentityService's login flow drives AccessFailedAsync / IsLockedOutAsync.
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
           .AddEntityFrameworkStores<IdentityDbContext>()
           .AddDefaultTokenProviders();

        //metrics
        services.AddSingleton<IdentityMetrics>();

        // Skipped only by hosts that never authenticate a caller (the DbMigrator), which would
        // otherwise need a signing key purely to satisfy JwtOptions.ValidateOnStart().
        if (builder.GetHeroPlatformOptions().EnableAuthentication)
        {
            services.ConfigureJwtAuth();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var apiVersionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints
            .MapGroup("api/v{version:apiVersion}/identity")
            .WithTags("Identity")
            .WithApiVersionSet(apiVersionSet);

        // ── Anonymous, tenant-scoped auth routes (ADR-0002) ───────────────────────────
        // No token exists yet on these calls, so the tenant rides in the route instead:
        // /api/v1/tenants/{tenant}/auth/... . TenantFromRouteAttribute is the metadata that
        // permits tenant resolution to read that route value at all — and only while the caller
        // is anonymous. Every other endpoint takes its tenant from the signed token's claim.
        var authGroup = endpoints
            .MapGroup(TenantRoute.AnonymousAuthGroup)
            .WithTags("Auth")
            .WithApiVersionSet(apiVersionSet)
            .WithMetadata(new TenantFromRouteAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("auth")
            // `{tenant}` is a route value, not a handler argument: tenant resolution reads it and no
            // endpoint binds it. ASP.NET Core only documents the parameters it binds, so the exported
            // contract (ADR-0004) would carry a path template with an undeclared placeholder and a
            // generated client would have nothing to substitute. Declare it once, for the group.
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                if (!operation.Parameters.Any(p =>
                        p.In == ParameterLocation.Path &&
                        string.Equals(p.Name, TenantRoute.ValueKey, StringComparison.Ordinal)))
                {
                    operation.Parameters.Insert(0, new OpenApiParameter
                    {
                        Name = TenantRoute.ValueKey,
                        In = ParameterLocation.Path,
                        Required = true,
                        Description = "The tenant identifier. The one place a caller may name a tenant (ADR-0002).",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    });
                }

                return Task.CompletedTask;
            });

        authGroup.MapGenerateTokenEndpoint();
        authGroup.MapRefreshTokenEndpoint();
        authGroup.MapEndSessionEndpoint();
        authGroup.MapForgotPasswordEndpoint();
        authGroup.MapResetPasswordEndpoint();
        authGroup.MapConfirmEmailEndpoint();
        authGroup.MapSelfRegisterUserEndpoint();

        // The outbox is dispatched by the framework's OutboxDispatcherHostedService (on by default), which now claims
        // rows with FOR UPDATE SKIP LOCKED so several instances can drain safely. This module still registers no
        // dispatcher of its own: the outbox is framework infrastructure, not Identity's.

        // roles
        group.MapGetRolesEndpoint();
        group.MapGetRoleByIdEndpoint();
        group.MapDeleteRoleEndpoint();
        group.MapGetRolePermissionsEndpoint();
        group.MapUpdateRolePermissionsEndpoint();
        group.MapCreateOrUpdateRoleEndpoint();

        // permission catalog — every permission registered with the host,
        // filtered to the caller's tenant context (root vs admin set)
        group.MapGetPermissionCatalogEndpoint();

        // users
        group.MapAssignUserRolesEndpoint();
        group.MapChangePasswordEndpoint();
        group.MapAdminConfirmEmailEndpoint();
        group.MapResendConfirmationEmailEndpoint().RequireRateLimiting("auth");
        group.MapDeleteUserEndpoint();
        group.MapGetUserByIdEndpoint();
        group.MapGetCurrentUserPermissionsEndpoint();
        group.MapGetMeEndpoint();
        group.MapGetUserRolesEndpoint();
        group.MapGetUsersListEndpoint();
        group.MapSearchUsersEndpoint();
        group.MapRegisterUserEndpoint();
        group.MapToggleUserStatusEndpoint();
        group.MapUpdateUserEndpoint();
        group.MapSetProfileImageEndpoint();

        // sessions - user endpoints
        group.MapGetMySessionsEndpoint();
        group.MapRevokeSessionEndpoint();
        group.MapRevokeAllSessionsEndpoint();

        // sessions - admin endpoints
        group.MapGetTenantSessionsEndpoint();
        group.MapGetUserSessionsEndpoint();
        group.MapAdminRevokeSessionEndpoint();
        group.MapAdminRevokeAllSessionsEndpoint();

        // groups
        group.MapGetGroupsEndpoint();
        group.MapGetGroupByIdEndpoint();
        group.MapCreateGroupEndpoint();
        group.MapUpdateGroupEndpoint();
        group.MapDeleteGroupEndpoint();
        group.MapGetGroupMembersEndpoint();
        group.MapAddUsersToGroupEndpoint();
        group.MapRemoveUserFromGroupEndpoint();

        // user groups
        group.MapGetUserGroupsEndpoint();

        // operator — cross-tenant token exchange (ADR-0002). Root-only; NOT in the anonymous
        // tenants/{tenant}/auth group: the caller already holds a signed root token.
        group.MapExchangeOperatorTokenEndpoint();

        // impersonation
        group.MapStartImpersonationEndpoint();
        group.MapEndImpersonationEndpoint();
        group.MapGetImpersonationGrantsEndpoint();
        group.MapRevokeImpersonationGrantEndpoint();

        // two-factor authentication (TOTP)
        group.MapEnrollTwoFactorEndpoint();
        group.MapVerifyEnrollTwoFactorEndpoint();
        group.MapDisableTwoFactorEndpoint();
    }
}