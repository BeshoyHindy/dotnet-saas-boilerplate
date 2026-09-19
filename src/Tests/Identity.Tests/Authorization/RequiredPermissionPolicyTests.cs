using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Claims;

namespace Identity.Tests.Authorization;

/// <summary>
/// Drives the RequiredPermission policy through its public seam (<see cref="IAuthorizationService"/>)
/// with a real <see cref="Endpoint"/> as the resource — the same shape the authorization middleware
/// passes. Covers the two rules the policy exists for: every listed permission must be held (all-of),
/// and an endpoint that declares no intent is denied (fail-closed).
/// </summary>
public sealed class RequiredPermissionPolicyTests
{
    private const string PermissionA = "Permissions.Users.View";
    private const string PermissionB = "Permissions.Users.Update";
    private const string UserId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Policy_Should_Deny_When_UserHoldsOnlyOneOfTwoRequiredPermissions()
    {
        var endpoint = EndpointWith(new RequiredPermissionAttribute(PermissionA, PermissionB));

        var result = await AuthorizeAsync(endpoint, PermissionA);

        result.Succeeded.ShouldBeFalse(
            "RequiredPermissions is an all-of set — holding one of two must not open the endpoint.");
    }

    [Fact]
    public async Task Policy_Should_Allow_When_UserHoldsEveryRequiredPermission()
    {
        var endpoint = EndpointWith(new RequiredPermissionAttribute(PermissionA, PermissionB));

        var result = await AuthorizeAsync(endpoint, PermissionA, PermissionB);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Policy_Should_Deny_When_EndpointDeclaresNoIntent()
    {
        var endpoint = EndpointWith();

        var result = await AuthorizeAsync(endpoint, PermissionA, PermissionB);

        result.Succeeded.ShouldBeFalse(
            "An endpoint with neither a permission, AllowAnonymous nor RequireAuthenticatedOnly must fail closed.");
    }

    [Fact]
    public async Task Policy_Should_Deny_When_RequiredPermissionSetIsEmpty()
    {
        var endpoint = EndpointWith(new RequiredPermissionAttribute(requiredPermission: null));

        var result = await AuthorizeAsync(endpoint, PermissionA, PermissionB);

        result.Succeeded.ShouldBeFalse(
            "An empty RequiredPermissions set declares nothing — it must not be treated as satisfied.");
    }

    [Fact]
    public async Task Policy_Should_Allow_When_EndpointIsMarkedAuthenticatedOnly()
    {
        var endpoint = EndpointWith(new AuthenticatedOnlyAttribute());

        var result = await AuthorizeAsync(endpoint);

        result.Succeeded.ShouldBeTrue(
            "The policy already requires an authenticated user; the marker says that is the whole gate.");
    }

    [Fact]
    public async Task Policy_Should_Deny_When_ResourceIsNotAnEndpoint()
    {
        var result = await AuthorizeAsync(resource: null, PermissionA, PermissionB);

        result.Succeeded.ShouldBeFalse("Without an endpoint there is no declared intent to honour.");
    }

    private static Endpoint EndpointWith(params object[] metadata)
    {
        return new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "test-endpoint");
    }

    private static async Task<AuthorizationResult> AuthorizeAsync(object? resource, params string[] heldPermissions)
    {
        var userService = Substitute.For<IUserService>();
        userService
            .HasPermissionAsync(UserId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(heldPermissions.Contains(call.ArgAt<string>(1), StringComparer.Ordinal)));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userService);
        services.AddAuthorizationBuilder().AddRequiredPermissionPolicy();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var authorizationService = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        return await authorizationService.AuthorizeAsync(
            AuthenticatedUser(),
            resource,
            RequiredPermissionDefaults.PolicyName);
    }

    private static ClaimsPrincipal AuthenticatedUser()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId)],
            authenticationType: "Test"));
    }
}
