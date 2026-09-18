using Mediator;

namespace Boilerplate.Modules.Billing.Contracts.v1.Wallets;

/// <summary>
/// Operator command — rejects a Pending top-up request. Returns the request id.
/// </summary>
public sealed record RejectTopupRequestCommand(Guid Id, string? Reason) : ICommand<Guid>;
