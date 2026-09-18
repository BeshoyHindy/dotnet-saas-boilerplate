using Mediator;

namespace Boilerplate.Modules.Billing.Contracts.v1.Invoices;

public sealed record IssueInvoiceCommand(Guid InvoiceId, DateTime? DueAtUtc = null) : ICommand<Guid>;
