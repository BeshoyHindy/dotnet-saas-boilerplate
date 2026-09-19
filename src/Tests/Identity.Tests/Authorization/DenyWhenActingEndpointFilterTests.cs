using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Identity.Tests.Authorization;

/// <summary>
/// Drives <see cref="DenyWhenActingEndpointFilter"/> directly through <see cref="IEndpointFilter"/> —
/// the filter is the whole enforcement point for "an actor must not touch the subject's own
/// credentials", so it is covered without spinning up a host.
/// </summary>
public sealed class DenyWhenActingEndpointFilterTests
{
    private readonly DenyWhenActingEndpointFilter _filter = new();

    [Fact]
    public async Task InvokeAsync_Should_Throw_Forbidden_When_TokenCarriesActorSubject()
    {
        var context = InvocationContextFor(ActingUser());

        var act = () => _filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("unreachable")).AsTask();

        await act.ShouldThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task InvokeAsync_Should_CallNext_When_TokenHasNoActorSubject()
    {
        var context = InvocationContextFor(SubjectOnlyUser());
        var expected = new object();

        var result = await _filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(expected));

        result.ShouldBeSameAs(expected, "with no act_sub claim the request is the caller's own — the filter must be a no-op.");
    }

    private static DefaultEndpointFilterInvocationContext InvocationContextFor(ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user };
        return new DefaultEndpointFilterInvocationContext(httpContext);
    }

    private static ClaimsPrincipal ActingUser()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111"),
                new Claim(ClaimConstants.ActorSubject, "22222222-2222-2222-2222-222222222222")
            ],
            authenticationType: "Test"));
    }

    private static ClaimsPrincipal SubjectOnlyUser()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111")],
            authenticationType: "Test"));
    }
}
