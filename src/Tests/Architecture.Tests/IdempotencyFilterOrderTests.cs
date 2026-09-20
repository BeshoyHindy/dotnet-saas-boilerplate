using Shouldly;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// <c>IdempotencyEndpointFilter</c> writes the response itself and hands the pipeline back
/// <c>TypedResults.Empty</c>. Endpoint filters run outermost-first in the order they are added, so a
/// filter added <i>before</i> <c>.WithIdempotency()</c> wraps it: it sees <c>Empty</c> where it
/// expected the handler's result, and whatever it does after <c>next</c> — inspect the result, set a
/// header — happens after the bytes have already gone out. A filter added after it is fine; it sits
/// between this one and the handler, and its result and headers are captured normally.
///
/// <para>The hazard has no runtime symptom on the endpoint that causes it: the response is still
/// correct, the wrapping filter is simply ignored. That is exactly the kind of thing worth failing a
/// build over rather than discovering from a missing audit entry.</para>
///
/// <para><b>What this scan sees.</b> Text, over the endpoint sources under <c>src/Modules/</c>, one
/// route chain at a time (<see cref="RouteChains"/>) — a file that maps two routes is two chains, and
/// a filter on the first says nothing about the second. Every filter-adding call in the kit is one of
/// the three names below; a filter added through a local variable, or a chain split across
/// statements, is outside its reach. The prose on the filter is the rule, this is the guard that
/// keeps it visible.</para>
/// </summary>
public sealed partial class IdempotencyFilterOrderTests
{
    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    /// <summary>
    /// Every call that adds an endpoint filter: the primitive itself, and the two extensions in the
    /// kit that wrap it. Add a new filter extension here when you add one.
    /// </summary>
    [GeneratedRegex(@"\.(AddEndpointFilter|DenyWhenActing|WithIdempotency)\b", RegexOptions.CultureInvariant)]
    private static partial Regex FilterCall();

    [Fact]
    public void WithIdempotency_Should_Be_The_First_EndpointFilter_On_Its_Route()
    {
        var offenders = new List<string>();
        var routesScanned = 0;
        var idempotentRoutes = 0;

        foreach (var file in EndpointSourceFiles())
        {
            var chains = RouteChains.Split(File.ReadAllText(file));
            routesScanned += chains.Count;

            foreach (var chain in chains.Where(c => c.Contains(".WithIdempotency", StringComparison.Ordinal)))
            {
                idempotentRoutes++;
                offenders.AddRange(Misordered(chain).Select(o => $"{Relative(file)}: {o}"));
            }
        }

        // Anti-vacuous: a scanner that stopped finding routes would pass in silence.
        routesScanned.ShouldBeGreaterThan(20, "the scan should be seeing every mapped route in the modules.");
        idempotentRoutes.ShouldBe(2, "the kit has two idempotent endpoints; update this when that changes.");

        offenders.ShouldBeEmpty(
            "the idempotency filter writes the response and returns Empty, so nothing may wrap it: put " +
            ".WithIdempotency() before every other endpoint filter on the route. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    #region The scanner itself

    [Fact]
    public void A_File_Mapping_Two_Routes_Should_Be_Judged_Per_Route()
    {
        // The false positive the per-file version had: the filter on the first route is not on the
        // second, and reading the whole file as one chain made it look like it was.
        const string Source = """
            public static class TwoRoutesEndpoint
            {
                internal static void Map(this IEndpointRouteBuilder endpoints)
                {
                    endpoints.MapPost("/change-password", async (ChangePasswordCommand command) =>
                    {
                        var result = await Send(command);
                        return TypedResults.Ok(result);
                    })
                    .DenyWhenActing();

                    endpoints.MapPost("/register", async (RegisterUserCommand command) =>
                    {
                        var result = await Send(command);
                        return TypedResults.Created("/users/1", result);
                    })
                    .WithIdempotency();
                }
            }
            """;

        var chains = RouteChains.Split(Source);

        chains.Count.ShouldBe(2, "two Map… calls are two routes.");
        chains[0].ShouldContain("DenyWhenActing");
        chains[0].ShouldNotContain("WithIdempotency");
        chains.SelectMany(Misordered).ShouldBeEmpty("neither route puts a filter in front of idempotency.");
    }

    [Fact]
    public void A_Genuinely_Misordered_Chain_Should_Be_Caught()
    {
        const string Source = """
            public static class WrappedEndpoint
            {
                internal static void Map(this IEndpointRouteBuilder endpoints)
                {
                    endpoints.MapPost("/register", async (RegisterUserCommand command) =>
                    {
                        var result = await Send(command);
                        return TypedResults.Created("/users/1", result);
                    })
                    .DenyWhenActing()
                    .WithIdempotency();
                }
            }
            """;

        var offenders = RouteChains.Split(Source).SelectMany(Misordered).ToList();

        offenders.ShouldHaveSingleItem();
        offenders[0].ShouldContain("DenyWhenActing");
    }

    [Fact]
    public void A_Filter_Added_After_Idempotency_Should_Be_Allowed()
    {
        // Ordering is the rule, not exclusivity: a filter added afterwards sits between this one and
        // the handler, where its result and headers are captured normally.
        const string Source = """
            endpoints.MapPost("/register", () => TypedResults.Ok())
                .WithIdempotency()
                .DenyWhenActing();
            """;

        RouteChains.Split(Source).SelectMany(Misordered).ShouldBeEmpty();
    }

    [Fact]
    public void A_Semicolon_Inside_A_Lambda_Or_A_String_Should_Not_End_TheChain()
    {
        // Why the scanner is depth- and literal-aware: "up to the next semicolon" would cut every
        // chain in this repository in the middle of its own handler.
        const string Source = """"
            endpoints.MapPost("/things", async (ThingCommand command) =>
            {
                var result = await Send(command);
                return TypedResults.Created($"/things/{result.Id};v=1", result);
            })
            .WithName("Thing")
            .WithIdempotency();
            """";

        var chains = RouteChains.Split(Source);

        chains.ShouldHaveSingleItem();
        chains[0].ShouldEndWith(".WithIdempotency();");
    }

    [Fact]
    public void A_Commented_Out_Call_Should_Be_Ignored()
    {
        // A comment explaining why a call is absent sits in the same text a regex-based test greps —
        // without stripping, a comment that names the method would read as a call the source never
        // makes. This is also why an endpoint's own comment may name .WithIdempotency() or
        // .AllowAnonymous() plainly: RouteChains removes it before any scanner sees the chain.
        const string Source = """
            endpoints.MapPost("/register", (RegisterUserCommand command) => TypedResults.Ok())
            // .WithIdempotency() // deliberately not idempotent: see #84
            /* .WithIdempotency() */
            .AllowAnonymous();
            """;

        var chains = RouteChains.Split(Source);

        chains.ShouldHaveSingleItem();
        chains[0].ShouldNotContain("WithIdempotency");
        chains[0].ShouldContain(".AllowAnonymous();");
    }

    #endregion

    /// <summary>
    /// Filter calls in this chain that precede <c>.WithIdempotency()</c>, described for the failure
    /// message. Empty for a chain that does not use idempotency at all.
    /// </summary>
    private static IEnumerable<string> Misordered(string chain)
    {
        var calls = FilterCall().Matches(chain);

        for (var i = 0; i < calls.Count; i++)
        {
            if (calls[i].Groups[1].Value == "WithIdempotency" && i > 0)
            {
                yield return $".{calls[i - 1].Groups[1].Value}() precedes .WithIdempotency()";
            }
        }
    }

    /// <summary>
    /// Every endpoint source in the modules — where a route chain is written. BuildingBlocks is
    /// excluded because the extensions themselves live there and would match their own definitions.
    /// </summary>
    internal static IEnumerable<string> EndpointSourceFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(SolutionRoot, "src", "Modules"), "*Endpoint.cs", SearchOption.AllDirectories)
            .Where(f => !BuildOutputRegex().IsMatch(Relative(f)));

    internal static string Relative(string file) =>
        Path.GetRelativePath(SolutionRoot, file).Replace('\\', '/');

    [GeneratedRegex(@"/(bin|obj)/", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutputRegex();
}
