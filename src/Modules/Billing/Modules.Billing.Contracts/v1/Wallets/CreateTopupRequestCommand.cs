using Mediator;

namespace Boilerplate.Modules.Billing.Contracts.v1.Wallets;

public sealed record CreateTopupRequestCommand(decimal Amount, string? Note) : ICommand<Guid>;
