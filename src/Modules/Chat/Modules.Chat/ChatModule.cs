using Asp.Versioning;
using FluentValidation;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Web.Modules;
using Boilerplate.BuildingBlocks.Web.Realtime;
using Boilerplate.Modules.Chat.Contracts.Authorization;
using Boilerplate.Modules.Chat.Data;
using Boilerplate.Modules.Chat.Features.v1.Channels.AddChannelMembers;
using Boilerplate.Modules.Chat.Features.v1.Channels.ArchiveChannel;
using Boilerplate.Modules.Chat.Features.v1.Channels.CreateChannel;
using Boilerplate.Modules.Chat.Features.v1.Channels.DiscoverChannels;
using Boilerplate.Modules.Chat.Features.v1.Channels.FindOrCreateDm;
using Boilerplate.Modules.Chat.Features.v1.Channels.GetChannelById;
using Boilerplate.Modules.Chat.Features.v1.Channels.ListMyChannels;
using Boilerplate.Modules.Chat.Features.v1.Channels.MarkChannelRead;
using Boilerplate.Modules.Chat.Features.v1.Channels.RemoveChannelMember;
using Boilerplate.Modules.Chat.Features.v1.Channels.RestoreChannel;
using Boilerplate.Modules.Chat.Features.v1.Channels.UpdateChannel;
using Boilerplate.Modules.Chat.Features.v1.Messages.DeleteMessage;
using Boilerplate.Modules.Chat.Features.v1.Messages.EditMessage;
using Boilerplate.Modules.Chat.Features.v1.Messages.GetPinnedMessages;
using Boilerplate.Modules.Chat.Features.v1.Messages.ListChannelMessages;
using Boilerplate.Modules.Chat.Features.v1.Messages.ListMessageReplies;
using Boilerplate.Modules.Chat.Features.v1.Messages.PinMessage;
using Boilerplate.Modules.Chat.Features.v1.Messages.SendMessage;
using Boilerplate.Modules.Chat.Features.v1.Messages.UnpinMessage;
using Boilerplate.Modules.Chat.Features.v1.Reactions.AddReaction;
using Boilerplate.Modules.Chat.Features.v1.Reactions.RemoveReaction;
using Boilerplate.Modules.Chat.Features.v1.Search;
using Boilerplate.Modules.Chat.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Boilerplate.Modules.Chat;

/// <summary>
/// Chat module: Slack-style messaging (DMs + group DMs + named channels). Module Order 800 places
/// it after Notifications (750) so the Notifications module can register integration-event handlers
/// before Chat starts publishing.
/// </summary>
public sealed class ChatModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        PermissionConstants.Register(ChatPermissions.All);

        builder.Services.AddHeroDbContext<ChatDbContext>();
        builder.Services.AddScoped<IDbInitializer, ChatDbInitializer>();
        builder.Services.AddValidatorsFromAssembly(typeof(ChatModule).Assembly);

        // Realtime adapters consumed by AppHub (BuildingBlocks/Web). These let the shared hub
        // verify channel membership and pre-join channel groups without depending on Chat.
        builder.Services.AddScoped<IChannelMembershipChecker, ChannelMembershipChecker>();
        builder.Services.AddScoped<IUserChannelLookup, UserChannelLookup>();

        // @username resolution for SendMessage. Goes through Identity contracts so the user
        // directory stays the single source of truth.
        builder.Services.AddScoped<IMentionResolver, MentionResolver>();

        // File attachments: members attach+read, only the uploader deletes. Registered as
        // IFileAccessPolicy so Files endpoints route through it for OwnerType=ChatChannel.
        builder.Services.AddScoped<Boilerplate.Modules.Files.Contracts.IFileAccessPolicy, Authorization.ChatChannelFileAccessPolicy>();

        builder.Services.AddHealthChecks().AddDbContextCheck<ChatDbContext>(
            name: "db:chat",
            failureStatus: HealthStatus.Unhealthy);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints.MapGroup("api/v{version:apiVersion}/chat")
            .WithTags("Chat")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        // Channel reads — literal routes first
        group.MapListMyChannelsEndpoint();           // GET /channels
        group.MapDiscoverChannelsEndpoint();         // GET /channels/discover

        // Channel lifecycle
        group.MapCreateChannelEndpoint();
        group.MapFindOrCreateDmEndpoint();           // POST /dms — literal route comes before /{id}
        group.MapRestoreChannelEndpoint();           // literal /restore must precede catch-alls
        group.MapAddChannelMembersEndpoint();
        group.MapRemoveChannelMemberEndpoint();
        group.MapMarkChannelReadEndpoint();
        group.MapUpdateChannelEndpoint();
        group.MapArchiveChannelEndpoint();
        group.MapGetChannelByIdEndpoint();           // GET /channels/{id} — must follow literal routes

        // Messages
        group.MapListChannelMessagesEndpoint();      // GET /channels/{id}/messages
        group.MapListMessageRepliesEndpoint();       // GET /messages/{id}/replies
        group.MapGetPinnedMessagesEndpoint();        // GET /channels/{id}/pinned
        group.MapSendMessageEndpoint();              // POST /channels/{id}/messages
        group.MapEditMessageEndpoint();              // PUT /messages/{id}
        group.MapDeleteMessageEndpoint();            // DELETE /messages/{id}
        group.MapPinMessageEndpoint();               // POST /messages/{id}/pin
        group.MapUnpinMessageEndpoint();             // DELETE /messages/{id}/pin

        // Reactions
        group.MapAddReactionEndpoint();              // POST /messages/{id}/reactions
        group.MapRemoveReactionEndpoint();           // DELETE /messages/{id}/reactions/{emoji}

        // Search
        group.MapSearchMessagesEndpoint();           // GET /search
    }
}
