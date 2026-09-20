using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// A cheap, fast early warning for one of the two idempotency-hardening rules from #84/#85: for an
/// anonymous caller the idempotency partition has no subject to bind to, so it collapses to tenant +
/// <c>anon</c> + method + path + the caller-supplied <c>Idempotency-Key</c> alone, and anyone who
/// presents another caller's key on that route is handed their stored response.
/// <c>SelfRegisterUserEndpoint</c> was the one route in the kit that combined <c>.AllowAnonymous()</c>
/// and <c>.WithIdempotency()</c> in its own chain — it does not anymore, and a sequential retry did not
/// need it: <c>UserRegistrationService</c> already refuses a duplicate email/username with 400 rather
/// than creating a second user.
///
/// <para><b>What this scan sees, and what it cannot.</b> The same unit as
/// <see cref="IdempotencyFilterOrderTests"/> — one route chain at a time (<see cref="RouteChains"/>,
/// comments stripped), over the endpoint sources under <c>src/Modules/</c>. It matches
/// <c>.AllowAnonymous()</c> and <c>[AllowAnonymous]</c> written <i>on that route's own chain</i>. It
/// cannot see anonymity declared on a <c>MapGroup(...)</c> the route was mapped into — a route-group
/// <c>.AllowAnonymous()</c> (like <c>IdentityModule</c>'s <c>TenantRoute.AnonymousAuthGroup</c>) never
/// appears in the text of the file that maps the individual route, so a source-text scan is
/// structurally blind to it. <c>IdempotentEndpointAnonymityTests</c> (Integration.Tests) is the
/// authority: it reads the running host's built endpoint metadata, where a route-group,
/// <c>[AllowAnonymous]</c> attribute, and chain-call declaration are indistinguishable — this test is
/// the fast build-time signal for the common case, not a substitute for it.</para>
/// </summary>
public sealed class AnonymousRoutesAreNeverIdempotentTests
{
    [Fact]
    public void No_Route_Should_Combine_AllowAnonymous_And_WithIdempotency()
    {
        var offenders = new List<string>();
        var routesScanned = 0;

        foreach (var file in IdempotencyFilterOrderTests.EndpointSourceFiles())
        {
            var chains = RouteChains.Split(File.ReadAllText(file));
            routesScanned += chains.Count;

            offenders.AddRange(chains
                .Where(IsAnonymousAndIdempotent)
                .Select(_ => IdempotencyFilterOrderTests.Relative(file)));
        }

        // Anti-vacuous: a scanner that stopped finding routes would pass in silence.
        routesScanned.ShouldBeGreaterThan(20, "the scan should be seeing every mapped route in the modules.");

        offenders.ShouldBeEmpty(
            "an anonymous caller has no subject to bind the idempotency partition to, so the caller-" +
            "supplied key alone would hand a guessed key someone else's stored response. Drop " +
            ".WithIdempotency() from these routes:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void An_Anonymous_And_Idempotent_Chain_Should_Be_Caught()
    {
        const string Source = """
            public static class OffendingEndpoint
            {
                internal static void Map(this IEndpointRouteBuilder endpoints)
                {
                    endpoints.MapPost("/register", async (RegisterUserCommand command) =>
                    {
                        var result = await Send(command);
                        return TypedResults.Created("/users/1", result);
                    })
                    .AllowAnonymous()
                    .WithIdempotency();
                }
            }
            """;

        var chains = RouteChains.Split(Source);

        chains.Count(IsAnonymousAndIdempotent).ShouldBe(1);
    }

    [Fact]
    public void An_AllowAnonymous_Attribute_And_Idempotent_Chain_Should_Be_Caught()
    {
        // The attribute form: [AllowAnonymous] on the handler lambda rather than a chain call.
        // GenerateTokenEndpoint, RefreshTokenEndpoint and EndSessionEndpoint all use it.
        const string Source = """
            public static class OffendingAttributeEndpoint
            {
                internal static void Map(this IEndpointRouteBuilder endpoints)
                {
                    endpoints.MapPost("/token", [AllowAnonymous] async (LoginCommand command) =>
                    {
                        var result = await Send(command);
                        return TypedResults.Ok(result);
                    })
                    .WithIdempotency();
                }
            }
            """;

        var chains = RouteChains.Split(Source);

        chains.Count(IsAnonymousAndIdempotent).ShouldBe(1);
    }

    [Fact]
    public void A_File_With_One_Anonymous_And_One_Idempotent_Route_Should_Pass()
    {
        // The false positive a per-file (rather than per-chain) scan would produce: neither route
        // alone combines both calls, even though the file's text contains both somewhere in it.
        const string Source = """
            public static class TwoRoutesEndpoint
            {
                internal static void Map(this IEndpointRouteBuilder endpoints)
                {
                    endpoints.MapPost("/login", async (LoginCommand command) =>
                    {
                        var result = await Send(command);
                        return TypedResults.Ok(result);
                    })
                    .AllowAnonymous();

                    endpoints.MapPost("/create-tenant", async (CreateTenantCommand command) =>
                    {
                        var result = await Send(command);
                        return TypedResults.Created("/tenants/1", result);
                    })
                    .WithIdempotency();
                }
            }
            """;

        var chains = RouteChains.Split(Source);

        chains.Count.ShouldBe(2);
        chains.Count(IsAnonymousAndIdempotent).ShouldBe(0);
    }

    private static bool IsAnonymousAndIdempotent(string chain) =>
        (chain.Contains(".AllowAnonymous()", StringComparison.Ordinal)
            || chain.Contains("[AllowAnonymous]", StringComparison.Ordinal))
        && chain.Contains(".WithIdempotency()", StringComparison.Ordinal);
}
