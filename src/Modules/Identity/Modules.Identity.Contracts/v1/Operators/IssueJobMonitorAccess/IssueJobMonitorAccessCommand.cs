using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Operators.IssueJobMonitorAccess;

/// <summary>
/// A root operator asks to open the Job monitor from a browser (ADR-0009). The answer is a
/// short-lived credential for the Job monitor route only, bound to the caller's session.
/// </summary>
public sealed record IssueJobMonitorAccessCommand : ICommand<JobMonitorAccessGrant>;

/// <summary>
/// What the handler mints. <see cref="Token"/> never leaves the server in a response body: the
/// endpoint delivers it as an HttpOnly cookie and answers with <see cref="JobMonitorAccessResponse"/>.
/// </summary>
public sealed record JobMonitorAccessGrant(string Token, DateTime ExpiresAtUtc);

/// <summary>
/// The cookie has been set. <paramref name="Path"/> is where the Job monitor lives on this origin;
/// open it before <paramref name="ExpiresAt"/>, after which the operator asks again.
/// </summary>
public sealed record JobMonitorAccessResponse(string Path, DateTime ExpiresAt);
