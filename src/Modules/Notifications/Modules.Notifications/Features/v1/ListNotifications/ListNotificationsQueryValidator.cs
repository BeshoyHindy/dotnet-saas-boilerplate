using FluentValidation;
using Boilerplate.BuildingBlocks.Web.Validation;
using Boilerplate.Modules.Notifications.Contracts.v1.Queries;

namespace Boilerplate.Modules.Notifications.Features.v1.ListNotifications;

public sealed class ListNotificationsQueryValidator : AbstractValidator<ListNotificationsQuery>
{
    public ListNotificationsQueryValidator()
    {
        Include(new PagedQueryValidator<ListNotificationsQuery>());
    }
}
