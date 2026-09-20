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
/// <para><b>What this scan sees.</b> Text, over the endpoint sources under <c>src/Modules/</c>. A
/// route's builder chain is written as one statement in this codebase, so "appears earlier in the
/// file" is "appears earlier in the chain" — and every filter-adding call in the kit is one of the
/// three names below. A filter added through a variable, or in a chain split across statements, is
/// outside its reach; the prose on the filter is the rule, this is the guard that keeps it visible.
/// </para>
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

        foreach (var file in EndpointSourceFiles())
        {
            var text = File.ReadAllText(file);
            var calls = FilterCall().Matches(text);
            if (calls.Count < 2)
            {
                continue;
            }

            for (var i = 0; i < calls.Count; i++)
            {
                if (calls[i].Groups[1].Value == "WithIdempotency" && i > 0)
                {
                    offenders.Add($"{Relative(file)}: .{calls[i - 1].Groups[1].Value}() precedes .WithIdempotency()");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "the idempotency filter writes the response and returns Empty, so nothing may wrap it: put " +
            ".WithIdempotency() before every other endpoint filter on the route. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every endpoint source in the modules — where a route chain is written. BuildingBlocks is
    /// excluded because the extensions themselves live there and would match their own definitions.
    /// </summary>
    private static IEnumerable<string> EndpointSourceFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(SolutionRoot, "src", "Modules"), "*Endpoint.cs", SearchOption.AllDirectories)
            .Where(f => !BuildOutputRegex().IsMatch(Relative(f)));

    private static string Relative(string file) =>
        Path.GetRelativePath(SolutionRoot, file).Replace('\\', '/');

    [GeneratedRegex(@"/(bin|obj)/", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutputRegex();
}
