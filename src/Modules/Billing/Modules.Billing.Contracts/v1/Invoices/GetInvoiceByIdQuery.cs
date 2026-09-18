using Boilerplate.Modules.Billing.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Billing.Contracts.v1.Invoices;

public sealed record GetInvoiceByIdQuery(Guid InvoiceId) : IQuery<InvoiceDto>;
