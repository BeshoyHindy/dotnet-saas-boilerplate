using Mediator;

namespace Boilerplate.Modules.Billing.Contracts.v1.Invoices;

public sealed record VoidInvoiceCommand(Guid InvoiceId, string? Reason = null) : ICommand<Guid>;
