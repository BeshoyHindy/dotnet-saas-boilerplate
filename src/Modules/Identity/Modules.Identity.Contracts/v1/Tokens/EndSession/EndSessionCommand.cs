using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Tokens.EndSession;

/// <summary>
/// Ends one device's session — the command behind <c>POST /api/v1/tenants/{tenant}/auth/logout</c>.
/// Named for what it does to the session store rather than for the route, matching its sibling
/// <c>EndImpersonationCommand</c>.
///
/// The refresh token is optional: a caller holding a live access token is identified by its
/// <c>sid</c> claim, and the token is the fallback for a caller whose access token has expired —
/// which is the normal browser case.
/// </summary>
public sealed record EndSessionCommand(string? RefreshToken) : ICommand<Unit>;
