using Boilerplate.Modules.Billing.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Billing.Contracts.v1.Wallets;

public sealed record GetMyWalletQuery : IQuery<WalletDto>;
