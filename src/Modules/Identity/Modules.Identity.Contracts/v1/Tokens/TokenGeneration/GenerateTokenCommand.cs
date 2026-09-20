using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;

public record GenerateTokenCommand(
    string Email,
    string Password,
    string? TwoFactorCode = null)
    : ICommand<TokenResponse>;