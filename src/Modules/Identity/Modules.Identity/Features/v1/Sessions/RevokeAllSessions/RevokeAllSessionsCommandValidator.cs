using FluentValidation;
using Boilerplate.Modules.Identity.Contracts.v1.Sessions.RevokeAllSessions;

namespace Boilerplate.Modules.Identity.Features.v1.Sessions.RevokeAllSessions;

public sealed class RevokeAllSessionsCommandValidator : AbstractValidator<RevokeAllSessionsCommand>
{
    public RevokeAllSessionsCommandValidator()
    {
        // ExceptSessionId is optional - no validation required
        // This validator exists for consistency and potential future validation rules
    }
}