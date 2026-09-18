using Asp.Versioning;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Web.Modules;
using Boilerplate.Modules.Tickets.Contracts.Authorization;
using Boilerplate.Modules.Tickets.Data;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.AddTicketComment;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.AssignTicket;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.CloseTicket;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.CreateTicket;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.DeleteTicket;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.GetTicketById;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.ListTicketComments;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.ListTrashedTickets;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.ReopenTicket;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.ResolveTicket;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.RestoreTicket;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.SearchTickets;
using Boilerplate.Modules.Tickets.Features.v1.Tickets.UpdateTicket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

[assembly: AppModule(typeof(Boilerplate.Modules.Tickets.TicketsModule), 700)]

namespace Boilerplate.Modules.Tickets;

public sealed class TicketsModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        PermissionConstants.Register(TicketsPermissions.All);

        builder.Services.AddHeroDbContext<TicketsDbContext>();
        builder.Services.AddScoped<IDbInitializer, TicketsDbInitializer>();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<TicketsDbContext>(
                name: "db:tickets",
                failureStatus: HealthStatus.Unhealthy);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        // No custom middleware needed
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints
            .MapGroup("api/v{version:apiVersion}")
            .WithTags("Tickets")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        // Trash + comment routes register before the catch-all `{ticketId:guid}` GET so literal
        // segments win — minimal APIs match the first compatible pattern, so order matters.
        group.MapListTrashedTicketsEndpoint();
        group.MapAddTicketCommentEndpoint();
        group.MapListTicketCommentsEndpoint();

        group.MapRestoreTicketEndpoint();
        group.MapAssignTicketEndpoint();
        group.MapResolveTicketEndpoint();
        group.MapReopenTicketEndpoint();
        group.MapCloseTicketEndpoint();

        group.MapCreateTicketEndpoint();
        group.MapSearchTicketsEndpoint();
        group.MapUpdateTicketEndpoint();
        group.MapDeleteTicketEndpoint();
        group.MapGetTicketByIdEndpoint();
    }
}
