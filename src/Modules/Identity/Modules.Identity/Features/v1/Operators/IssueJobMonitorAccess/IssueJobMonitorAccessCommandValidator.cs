using Boilerplate.Modules.Identity.Contracts.v1.Operators.IssueJobMonitorAccess;
using FluentValidation;

namespace Boilerplate.Modules.Identity.Features.v1.Operators.IssueJobMonitorAccess;

/// <summary>
/// The command has no input: who is asking, and for which session, comes from the caller's token.
/// Present so the command stays paired with a validator like every other.
/// </summary>
public sealed class IssueJobMonitorAccessCommandValidator : AbstractValidator<IssueJobMonitorAccessCommand>;
