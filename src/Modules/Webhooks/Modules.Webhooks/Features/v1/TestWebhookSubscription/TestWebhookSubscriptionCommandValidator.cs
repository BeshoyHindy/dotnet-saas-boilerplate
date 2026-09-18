using FluentValidation;
using Boilerplate.Modules.Webhooks.Contracts.v1.TestWebhookSubscription;

namespace Boilerplate.Modules.Webhooks.Features.v1.TestWebhookSubscription;

public sealed class TestWebhookSubscriptionCommandValidator : AbstractValidator<TestWebhookSubscriptionCommand>
{
    public TestWebhookSubscriptionCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
