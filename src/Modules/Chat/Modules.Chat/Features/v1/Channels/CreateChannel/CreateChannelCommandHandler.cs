using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Chat.Contracts.v1.Commands;
using Boilerplate.Modules.Chat.Data;
using Boilerplate.Modules.Chat.Domain;
using Mediator;

namespace Boilerplate.Modules.Chat.Features.v1.Channels.CreateChannel;

public sealed class CreateChannelCommandHandler(
    ChatDbContext db,
    ICurrentUser currentUser)
    : ICommandHandler<CreateChannelCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateChannelCommand cmd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var userId = currentUser.GetUserId().ToString();
        if (userId == Guid.Empty.ToString())
        {
            throw new UnauthorizedException("no current user");
        }

        var channel = ChatChannel.CreateChannel(cmd.Name, cmd.Description, cmd.IsPrivate, userId);
        db.Channels.Add(channel);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return channel.Id;
    }
}
