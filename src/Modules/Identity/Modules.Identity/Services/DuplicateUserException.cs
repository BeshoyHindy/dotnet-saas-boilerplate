using Boilerplate.BuildingBlocks.Core.Exceptions;
using Microsoft.AspNetCore.Identity;
using System.Net;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// A create ASP.NET Identity refused because the address or the username is already held in this
/// tenant (#86).
///
/// It is a <see cref="CustomException"/> with the message and the 400 the caller would have got
/// anyway, so letting it escape changes nothing a client can see. What it adds is
/// <see cref="Kind"/> — which of the two collided — so the external-sign-in path can tell the race
/// it can recover from (the address is taken: that user IS the caller) from the one it must not
/// (only the username is taken: that user is a stranger who happens to have picked the same name).
/// </summary>
internal sealed class DuplicateUserException : CustomException
{
    public DuplicateUserException(string message, IdentityResult result)
        : base(
            message,
            (result?.Errors ?? []).Select(error => error.Description).ToList(),
            HttpStatusCode.BadRequest)
    {
        Kind = RegistrationConflict.Describe(result);
    }

    // The framework-shaped constructors CA1032 asks for. Nothing raises a duplicate without an
    // IdentityResult to say what collided, so these carry no Kind and claim no collision.
    public DuplicateUserException()
        : base()
    {
    }

    public DuplicateUserException(string message)
        : base(message)
    {
    }

    public DuplicateUserException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the refused create collided with.</summary>
    public RegistrationConflictKind Kind { get; }
}
