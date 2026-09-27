using System.Net;
using Boilerplate.BuildingBlocks.Core.Exceptions;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// The profile changed since the caller read it: a stale <c>If-Match</c> from optimistic
/// concurrency checking, or Identity's <c>ConcurrencyFailure</c> when another save lands between
/// the load and this one. Answered as 412 ProblemDetails; the caller should reload the profile
/// rather than resend the same body, which would overwrite the change it has not seen.
/// </summary>
internal sealed class ProfileChangedException : CustomException
{
    private const string DefaultMessage =
        "The profile was changed since it was read. Reload it and apply your changes again.";

    public ProfileChangedException()
        : this(DefaultMessage)
    {
    }

    // The framework-shaped constructors CA1032 asks for; every one of them is still a 412.
    public ProfileChangedException(string message)
        : base(message, errors: null, HttpStatusCode.PreconditionFailed)
    {
    }

    public ProfileChangedException(string message, Exception innerException)
        : base(message, innerException, HttpStatusCode.PreconditionFailed)
    {
    }
}
