using Boilerplate.Modules.Identity.Contracts.v1.Tokens.EndSession;
using FluentValidation;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.EndSession;

public sealed class EndSessionCommandValidator : AbstractValidator<EndSessionCommand>
{
    /// <summary>
    /// A well-formed token is a tenant slug (≤63) plus a 43-character secret; the cap only stops an
    /// anonymous caller posting an unbounded string into the hash on an endpoint that must answer
    /// 204 no matter what.
    /// </summary>
    public const int MaxRefreshTokenLength = 512;

    public EndSessionCommandValidator()
    {
        // RefreshToken is deliberately optional — a caller with a live access token is identified by
        // its `sid` claim, and a browser's token arrives in the HttpOnly cookie, not the body.
        RuleFor(p => p.RefreshToken)
            .MaximumLength(MaxRefreshTokenLength)
            .When(p => p.RefreshToken is not null);
    }
}
