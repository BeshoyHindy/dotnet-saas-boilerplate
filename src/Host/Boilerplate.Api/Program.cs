using Boilerplate.BuildingBlocks.Eventing;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Boilerplate.BuildingBlocks.Web;
using Boilerplate.BuildingBlocks.Web.Configuration;
using Boilerplate.BuildingBlocks.Web.Modules;
using Boilerplate.Modules.Auditing;
using Boilerplate.Modules.Identity;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using Boilerplate.Modules.Identity.Features.v1.Tokens.TokenGeneration;
using Boilerplate.Modules.Multitenancy;
using Boilerplate.Modules.Multitenancy.Contracts.v1.GetTenantStatus;
using Boilerplate.Modules.Multitenancy.Features.v1.GetTenantStatus;
using System.Reflection;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Serialize enums as string names (reads still accept names or integers). [Flags] enums (AuditTag, BodyCapture)
// opt back to numeric via their own NumericEnumConverter since comma-joined flag strings break bitwise consumers. Frontends mirror this as string unions.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Refuse to boot in Production with missing settings, placeholder secrets or an open host
// allow-list. No-op in every other environment.
builder.ValidateProductionConfiguration();

builder.Services.AddMediator(o =>
{
    o.ServiceLifetime = ServiceLifetime.Scoped;
    o.Assemblies = [
        typeof(GenerateTokenCommand),
        typeof(GenerateTokenCommandHandler),
        typeof(GetTenantStatusQuery),
        typeof(GetTenantStatusQueryHandler),
        typeof(Boilerplate.Modules.Auditing.Contracts.AuditEnvelope),
        typeof(Boilerplate.Modules.Auditing.Persistence.AuditDbContext),
        typeof(Boilerplate.Modules.Files.Contracts.v1.Commands.RequestUploadUrlCommand),
        typeof(Boilerplate.Modules.Files.FilesModule),
        typeof(Boilerplate.Modules.Notifications.Contracts.v1.Commands.MarkNotificationReadCommand),
        typeof(Boilerplate.Modules.Notifications.NotificationsModule)];
});

var moduleAssemblies = new Assembly[]
{
    typeof(IdentityModule).Assembly,
    typeof(MultitenancyModule).Assembly,
    typeof(AuditingModule).Assembly,
    typeof(Boilerplate.Modules.Files.FilesModule).Assembly,
    typeof(Boilerplate.Modules.Notifications.NotificationsModule).Assembly,
};

builder.AddHeroPlatform(o =>
{
    o.EnableCaching = true;
    o.EnableMailing = true;
    o.EnableJobs = true;
});

// The transactional outbox is framework infrastructure with exactly one owner
// (EventingDbContext), so the host registers it once for every module (issue #1349).
builder.Services.AddEventingCore(builder.Configuration);

builder.AddModules(moduleAssemblies);

// Self-heal deployments carrying retired per-module `{module}-outbox-dispatcher` Hangfire recurring jobs
// (the outbox is now dispatched by OutboxDispatcherHostedService). No-op once the storage is clean.
builder.Services.AddHostedService<Boilerplate.Api.OrphanedOutboxRecurringJobCleanupService>();

var app = builder.Build();

// Finbuckle tenant resolution, deliberately before UseHeroPlatform (and so before
// UseAuthentication): resolution is header-driven, not claim-driven. This only installs the
// resolution middleware — per-tenant databases are migrated by the DbMigrator host.
app.UseMultiTenant();
app.UseHeroPlatform(p =>
{
    p.MapModules = true;
    p.ServeStaticFiles = true;
});

app.MapGet("/", () => Results.Ok(new { message = "hello world!" }))
   .WithTags("PlayGround")
   .AllowAnonymous();
await app.RunAsync();