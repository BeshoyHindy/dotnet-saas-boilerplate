using System.Globalization;
using System.Text.Json;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.TenantSweep;
using Microsoft.AspNetCore.Http;
using Xunit.Abstractions;

namespace Integration.Tests.Tests.Multitenancy;

/// <summary>
/// ADR-0002's acceptance test, on real PostgreSQL: <i>every</i> route the host publishes that names
/// a resource by id is called with tenant A's token and tenant B's id, and must answer 404 — not
/// 200, not 403, not 400.
///
/// Why each of those is a failure and not a nicety:
/// <list type="bullet">
///   <item><b>200</b> is the leak itself.</item>
///   <item><b>403</b> leaks existence. "You may not see this" and "this does not exist" are
///     different sentences, and only the second is true across a tenant boundary. It also means
///     authorization, not isolation, is what stopped the request — so the day someone widens a
///     permission, the row becomes readable.</item>
///   <item><b>400</b> means a validator fired before the lookup, so the request never reached the
///     question we are asking. The registry supplies a body sample for exactly those routes.</item>
/// </list>
///
/// The endpoint list comes from <c>EndpointDataSource</c> at run time, so a route added tomorrow is
/// swept tomorrow. A route the registry cannot seed a resource for fails
/// <see cref="Every_Resource_Endpoint_Is_Covered_By_The_Registry_Or_Explicitly_Exempt"/> with the
/// registry key to add; the only way out is <c>[TenantSweepExempt("reason")]</c> at the mapping site.
///
/// <para><b>Shape.</b> These are aggregating tests, not a theory-per-endpoint. xUnit resolves theory
/// data at discovery time, and the endpoint list does not exist until a host is built — enumerating
/// it would mean standing up a second Testcontainers host for the whole suite. Instead every test
/// collects failures and reports one line per endpoint, so a failure still names the route,
/// the verb, and what came back.</para>
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class TenantEndpointSweepTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public TenantEndpointSweepTests(AppWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private Task<TenantSweepFixture> SweepAsync() => TenantSweepFixture.GetAsync(_factory);

    private static IEnumerable<SweptEndpoint> ResourceEndpoints(TenantSweepFixture sweep) =>
        sweep.Endpoints.Where(e => e.Class == SweepClass.Resource);

    /// <summary>
    /// Coverage. This is the test that makes the acceptance criterion true: add an endpoint with an
    /// id and it is swept automatically, because if the sweep cannot seed a resource for it, the
    /// build goes red with the exact entry to write.
    /// </summary>
    [Fact]
    public async Task Every_Resource_Endpoint_Is_Covered_By_The_Registry_Or_Explicitly_Exempt()
    {
        var sweep = await SweepAsync();
        var uncovered = ResourceEndpoints(sweep)
            .Where(e => !e.IsExempt)
            .SelectMany(e => e.ResourceParameters
                .Where(p => !TenantSweepRegistry.ByRouteKey.ContainsKey(p.RegistryKey))
                .Select(p => $"{e.Name}  → no registry entry for key \"{p.RegistryKey}\" (parameter {{{p.Name}}})"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        uncovered.ShouldBeEmpty(
            "every endpoint that takes a resource id must be swept for cross-tenant access. Add the " +
            $"key to {nameof(TenantSweepRegistry)}.{nameof(TenantSweepRegistry.ByRouteKey)} pointing at " +
            $"the {nameof(ResourceKind)} that route addresses (and, if the route is a write whose " +
            $"validator runs before the lookup, a body sample to {nameof(TenantSweepBodies)}). If the " +
            "parameter genuinely is not a tenant-scoped resource, say so at the mapping site with " +
            ".ExemptFromTenantSweep(\"reason\").\n  " +
            string.Join("\n  ", uncovered));
    }

    /// <summary>
    /// The sweep proper. Tenant A's token, tenant B's ids, every verb.
    /// </summary>
    [Fact]
    public async Task Tenant_A_Gets_404_For_Every_Tenant_B_Resource_Id()
    {
        var sweep = await SweepAsync();
        var failures = new List<string>();

        foreach (var endpoint in Ordered(ResourceEndpoints(sweep).Where(e => !e.IsExempt && !e.IsRootOnly)))
        {
            var (path, routeValues) = Substitute(endpoint, sweep.B);
            using var response = await SendAsync(sweep.A.AdminClient, endpoint, path, routeValues, sweep.A);

            var verdict = await JudgeAsync(endpoint, response, sweep.B);
            if (verdict is not null)
            {
                failures.Add($"{endpoint.Name}\n      called as {endpoint.Method} {path}\n{verdict}");
            }
        }

        failures.ShouldBeEmpty(
            "ADR-0002: a tenant-A token must not be able to tell that a tenant-B row exists. 200 is the " +
            "leak; 403 answers the existence question anyway and puts isolation behind a permission; " +
            "400 means a validator fired before the lookup, so the probe proved nothing — give that " +
            $"route a body sample in {nameof(TenantSweepBodies)}.\n  " +
            string.Join("\n  ", failures));
    }

    /// <summary>
    /// The positive control, without which the sweep above is worthless: the same request shape with
    /// tenant A's <i>own</i> ids must not 404 (nor 401/403). A typo'd route or an unacceptable body
    /// would otherwise 404 for both tenants and the sweep would pass while testing nothing.
    ///
    /// Destructive verbs are excluded here and controlled separately below, on rows minted for the
    /// purpose — a DELETE control that succeeds would delete the very row the other cases probe.
    ///
    /// <para><b>The control demands a success, not merely "not a 404".</b> Accepting anything but
    /// 401/403/404 let a 400 (validator rejected the body), a 415 (no body at all where one is
    /// required) or a 500 stand in for a reached row — and each of those means the request died
    /// before the lookup, which is precisely the state the sweep's own 404 would be indistinguishable
    /// from.</para>
    /// </summary>
    [Fact]
    public async Task Positive_Control_Tenant_A_Reaches_Its_Own_Resources()
    {
        var sweep = await SweepAsync();
        var failures = new List<string>();

        foreach (var endpoint in Ordered(ResourceEndpoints(sweep)
            .Where(e => !e.IsExempt && !e.IsRootOnly && !IsDestructive(e))))
        {
            var (path, routeValues) = Substitute(endpoint, sweep.A);
            using var response = await SendAsync(sweep.A.AdminClient, endpoint, path, routeValues, sweep.A);

            // A handful of routes refuse the caller's own row on its STATE, with a status they could
            // never give for another tenant's id. Each is declared with the exact status expected.
            TenantSweepExceptions.ControlsThatAnswerFromState.TryGetValue(endpoint.Name, out var declared);
            bool refusedAsDeclared = declared.Status == (int)response.StatusCode && declared.Status != 0;

            if ((int)response.StatusCode >= 400 && !refusedAsDeclared)
            {
                failures.Add(
                    $"{endpoint.Name}\n      called as {endpoint.Method} {path}\n" +
                    $"      got {(int)response.StatusCode} {response.StatusCode} for the tenant's OWN resource" +
                    (declared.Status == 0
                        ? string.Empty
                        : $" (declared as {declared.Status}: {declared.Reason})") + "\n" +
                    $"      body: {Truncate(await response.Content.ReadAsStringAsync())}");
            }
        }

        var stale = TenantSweepExceptions.ControlsThatAnswerFromState.Keys
            .Where(name => !ResourceEndpoints(sweep).Any(e => string.Equals(e.Name, name, StringComparison.Ordinal)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        failures.AddRange(stale.Select(name =>
            $"{name}\n      is declared in " +
            $"{nameof(TenantSweepExceptions)}.{nameof(TenantSweepExceptions.ControlsThatAnswerFromState)} " +
            "but is not a route any more — delete the entry"));

        failures.ShouldBeEmpty(
            "the cross-tenant sweep is only meaningful if the same request SUCCEEDS against the " +
            "caller's own row. A 404 here means the sweep's 404 proves nothing; a 400 or 415 means the " +
            "request never reached the lookup, which proves just as little — usually a wrong " +
            $"{nameof(ResourceKind)} in the registry or a missing body sample in " +
            $"{nameof(TenantSweepBodies)}.\n  " +
            string.Join("\n  ", failures));
    }

    /// <summary>
    /// Root has no override left (ADR-0002: operators cross tenants by exchanging a token, not by
    /// privilege). A root token is still a token for the root tenant, so tenant-scoped rows in
    /// tenant B must be just as invisible to it.
    /// </summary>
    [Fact]
    public async Task Root_Token_Also_Gets_404_For_Tenant_Scoped_Resources_Of_Another_Tenant()
    {
        var sweep = await SweepAsync();
        var failures = new List<string>();

        foreach (var endpoint in Ordered(ResourceEndpoints(sweep)
            .Where(e => !e.IsExempt && !e.IsRootOnly && !AddressesPlatformWideRow(e))))
        {
            var (path, routeValues) = Substitute(endpoint, sweep.B);
            using var response = await SendAsync(sweep.RootClient, endpoint, path, routeValues, sweep.A);

            var verdict = await JudgeAsync(endpoint, response, sweep.B);
            if (verdict is not null)
            {
                failures.Add(
                    $"{endpoint.Name}\n      called as {endpoint.Method} {path} with a ROOT token\n{verdict}");
            }
        }

        failures.ShouldBeEmpty(
            "root is not a skeleton key. ADR-0002 removed the operator override: a root principal " +
            "reaches another tenant only by exchanging its token for one whose `tenant` claim is that " +
            "tenant. Anything reachable here is reachable without that exchange, and without its audit " +
            $"record.\n  {string.Join("\n  ", failures)}");
    }

    /// <summary>
    /// Root-only routes (their permission is flagged <c>IsRoot</c> in the catalog, e.g. the tenants
    /// group) are classified from metadata rather than opted out by hand. A tenant admin must be
    /// refused — 403 or 404, never a success — and that refusal is correct, not a leak.
    /// </summary>
    [Fact]
    public async Task Root_Only_Endpoints_Refuse_A_Tenant_Admin()
    {
        var sweep = await SweepAsync();
        var failures = new List<string>();

        foreach (var endpoint in Ordered(ResourceEndpoints(sweep).Where(e => !e.IsExempt && e.IsRootOnly)))
        {
            var (path, routeValues) = Substitute(endpoint, sweep.B);
            using var response = await SendAsync(sweep.A.AdminClient, endpoint, path, routeValues, sweep.A);

            if ((int)response.StatusCode < 400)
            {
                failures.Add(
                    $"{endpoint.Name}\n      called as {endpoint.Method} {path}\n" +
                    $"      a tenant admin got {(int)response.StatusCode} {response.StatusCode} on a root-only route\n" +
                    $"      required: {string.Join(", ", endpoint.RequiredPermissions)}");
            }
        }

        failures.ShouldBeEmpty(
            "a route gated by an IsRoot permission must be unreachable from a tenant admin's token, " +
            $"whatever id it names.\n  {string.Join("\n  ", failures)}");
    }

    /// <summary>
    /// Destructive verbs, run last and against rows minted for the purpose. Two things are asserted
    /// at once: DELETE with B's id 404s (already covered above, re-checked here in isolation from
    /// the other probes), and DELETE with A's own freshly-seeded id succeeds — the control that
    /// stops a 404-for-everything route from passing the sweep silently.
    /// </summary>
    [Fact]
    public async Task Positive_Control_For_Destructive_Verbs_Uses_Fresh_Rows()
    {
        var sweep = await SweepAsync();
        var failures = new List<string>();
        var controlled = 0;

        var destructive = Ordered(ResourceEndpoints(sweep)
            .Where(e => !e.IsExempt && !e.IsRootOnly && IsDestructive(e))).ToList();

        foreach (var endpoint in destructive)
        {
            if (TenantSweepFreshRows.DestructiveRoutesWithoutAControl.ContainsKey(endpoint.Name))
            {
                continue;
            }

            var fresh = await MintFreshAsync(sweep, endpoint);
            if (fresh is null)
            {
                failures.Add(
                    $"{endpoint.Name}\n      no fresh row could be minted for it, so this DELETE is " +
                    "never proven to work at all — its 404 for tenant B means nothing. Add a factory " +
                    $"to {nameof(TenantSweepFreshRows)} (per kind, or per route when the ids have to " +
                    $"agree), or name the route in " +
                    $"{nameof(TenantSweepFreshRows)}.{nameof(TenantSweepFreshRows.DestructiveRoutesWithoutAControl)} " +
                    "with the reason.");
                continue;
            }

            var (path, routeValues) = Substitute(endpoint, sweep.A, fresh);
            using var response = await SendAsync(sweep.A.AdminClient, endpoint, path, routeValues, sweep.A);
            controlled++;

            if ((int)response.StatusCode >= 400)
            {
                failures.Add(
                    $"{endpoint.Name}\n      called as {endpoint.Method} {path} on a row seeded moments before\n" +
                    $"      got {(int)response.StatusCode} {response.StatusCode}\n" +
                    $"      body: {Truncate(await response.Content.ReadAsStringAsync())}");
            }
        }

        _output.WriteLine(
            $"destructive routes     : {destructive.Count}\n" +
            $"  controlled           : {controlled}\n" +
            $"  declared without one : {destructive.Count(e => TenantSweepFreshRows.DestructiveRoutesWithoutAControl.ContainsKey(e.Name))}");

        failures.ShouldBeEmpty(
            "a destructive verb must reach the caller's own row, or its 404 for the other tenant says " +
            $"nothing about isolation.\n  {string.Join("\n  ", failures)}");
    }

    /// <summary>
    /// The list half. Every collection endpoint on the versioned API is called with tenant A's token
    /// and the body is searched for tenant B's marker and for the ids of every row seeded in B.
    ///
    /// Coverage here needs no registry: the marker is stamped into the display fields of every
    /// seeded row, so a new list endpoint that leaks B's rows is caught the first time it runs,
    /// whether or not anyone remembered it exists.
    ///
    /// <para><b>A list that does not answer 2xx is asserted about too.</b> Skipping on any non-2xx
    /// made the pass silently shrinkable: a list that started 400ing on the sweep's query string, or
    /// that grew a permission a tenant admin does not hold, would drop out of the sweep and the run
    /// would look exactly as green as before. Every refusal is therefore matched against
    /// <see cref="TenantSweepExceptions.ListsThatRefuseATenantAdmin"/> by name, a stale entry fails
    /// too, and the count actually asserted is printed next to the count discovered.</para>
    /// </summary>
    [Fact]
    public async Task No_List_Endpoint_Returns_Another_Tenants_Rows()
    {
        var sweep = await SweepAsync();
        var needles = NeedlesFor(sweep.B);

        var failures = new List<string>();
        var refused = new List<string>();
        var discovered = 0;
        var asserted = 0;

        foreach (var endpoint in Ordered(sweep.Endpoints
            .Where(e => e.Class == SweepClass.Collection && e.IsVersionedApi && !e.IsExempt)))
        {
            discovered++;
            var path = Materialise(endpoint.Template) + ListQuery;

            using var response = await sweep.A.AdminClient.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            bool expectedRefusal = TenantSweepExceptions.ListsThatRefuseATenantAdmin
                .ContainsKey(endpoint.Name);

            // A 5xx is never an acceptable answer: it is the sweep failing to ask its question, and
            // on this route it would hide a leak behind an exception page.
            if ((int)response.StatusCode >= 500)
            {
                failures.Add(
                    $"{endpoint.Name}\n      called as GET {path}\n" +
                    $"      answered {(int)response.StatusCode} {response.StatusCode} — the list pass " +
                    $"never got to look at a body\n      body: {Truncate(body)}");
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                refused.Add(endpoint.Name);

                if (!expectedRefusal)
                {
                    failures.Add(
                        $"{endpoint.Name}\n      called as GET {path}\n" +
                        $"      answered {(int)response.StatusCode} {response.StatusCode}, so it was NOT " +
                        "swept. Either fix the route, or — if a tenant admin is genuinely not allowed " +
                        $"here — name it in {nameof(TenantSweepExceptions)}." +
                        $"{nameof(TenantSweepExceptions.ListsThatRefuseATenantAdmin)} with the reason\n" +
                        $"      body: {Truncate(body)}");
                }

                continue;
            }

            if (expectedRefusal)
            {
                failures.Add(
                    $"{endpoint.Name}\n      called as GET {path}\n" +
                    $"      answered {(int)response.StatusCode} but is listed as refusing a tenant " +
                    $"admin. Delete the {nameof(TenantSweepExceptions.ListsThatRefuseATenantAdmin)} " +
                    "entry — the list is swept now and the exemption is stale.");
            }

            asserted++;
            var hits = needles
                .Where(needle => body.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (hits.Count > 0)
            {
                failures.Add(
                    $"{endpoint.Name}\n      called as GET {path}\n" +
                    $"      response contains tenant B's: {string.Join(", ", hits)}");
            }
        }

        _output.WriteLine(
            $"list endpoints discovered : {discovered}\n" +
            $"  asserted (2xx)          : {asserted}\n" +
            $"  refused, by design      : {refused.Count}\n" +
            string.Join("\n", refused.Select(name =>
                $"    {name}\n      {TenantSweepExceptions.ListsThatRefuseATenantAdmin.GetValueOrDefault(name, "UNEXPECTED")}")));

        failures.ShouldBeEmpty(
            "a list endpoint returned rows belonging to another tenant, or was not swept at all. Every " +
            "row the sweep seeds in tenant B carries that tenant's marker in its display fields, so any " +
            "appearance of it in a tenant-A response is a leak — and a list that answers anything but " +
            $"2xx asserts nothing, so it has to be a known, named refusal.\n  " +
            string.Join("\n  ", failures));

        asserted.ShouldBe(
            discovered - refused.Count,
            "every list endpoint that answered 2xx must have been searched for tenant B's rows");
    }

    /// <summary>
    /// After the sweep has fired every verb — including DELETE and PATCH — at tenant B's ids, tenant
    /// B's rows must still be there and still readable with B's own token. A 404 that was really a
    /// successful delete would otherwise pass every test above.
    ///
    /// <para>The bulk revoke gets a behavioural proof rather than a counted one. <c>POST
    /// identity/users/{userId}/sessions/revoke-all</c> answers 200 with <c>revokedCount: 0</c> for
    /// another tenant's user, and that zero is the handler's own account of itself. What the caller
    /// of ADR-0002 actually cares about is whether tenant B's user is still logged in — so the
    /// refresh token of B's seeded session is rotated here, with B's own tenant in the route. A
    /// revoked session cannot rotate.</para>
    /// </summary>
    [Fact]
    public async Task Tenant_B_Rows_Are_Untouched_After_The_Sweep()
    {
        var sweep = await SweepAsync();
        // Re-run the destructive half of the sweep first so this assertion follows it regardless of
        // the order xUnit picks; the calls are idempotent from B's point of view (they must all 404).
        foreach (var endpoint in Ordered(ResourceEndpoints(sweep)
            .Where(e => !e.IsExempt && !e.IsRootOnly && IsDestructive(e))))
        {
            var (path, routeValues) = Substitute(endpoint, sweep.B);
            using var _ = await SendAsync(sweep.A.AdminClient, endpoint, path, routeValues, sweep.A);
        }

        // And the bulk revoke, which is not a DELETE and so is not in the loop above.
        using (var revokeAll = await sweep.A.AdminClient.PostAsync(
            $"{TestConstants.IdentityBasePath}/users/{sweep.B[ResourceKind.User]}/sessions/revoke-all",
            content: null))
        {
            _output.WriteLine(
                $"revoke-all against tenant B's user → {(int)revokeAll.StatusCode}: " +
                Truncate(await revokeAll.Content.ReadAsStringAsync()));
        }

        var missing = new List<string>();

        foreach (var (kind, path, mustContain) in ReadBackPaths(sweep.B))
        {
            using var response = await sweep.B.AdminClient.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                missing.Add(
                    $"{kind} at GET {path} → {(int)response.StatusCode} {response.StatusCode}: " +
                    Truncate(body));
            }
            else if (!body.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(
                    $"{kind} at GET {path} answered 200 but no longer carries {mustContain}: " +
                    Truncate(body));
            }
        }

        // The session B's user is actually holding: rotating its refresh token is what proves the
        // cross-tenant revoke-all did nothing, in the only currency that matters to that user.
        using var rotated = await _factory.CreateClient().PostAsJsonAsync(
            $"{TestConstants.AuthBasePath(sweep.B.TenantId)}/refresh",
            new { refreshToken = sweep.B.Member.RefreshToken });

        if (!rotated.IsSuccessStatusCode)
        {
            missing.Add(
                "the session of tenant B's user no longer rotates its refresh token → " +
                $"{(int)rotated.StatusCode} {rotated.StatusCode}: " +
                Truncate(await rotated.Content.ReadAsStringAsync()));
        }

        missing.ShouldBeEmpty(
            "tenant B's rows must survive the sweep. If one is gone, a cross-tenant call did not just " +
            "answer 404 — it did the work first and reported 404 afterwards, which is the worst of " +
            $"both worlds.\n  {string.Join("\n  ", missing)}");
    }

    /// <summary>
    /// Prints what was swept, and pins it.
    ///
    /// The report is the record a reviewer reads. The four set assertions underneath it are what stop
    /// the sweep shrinking: a floor ("more than twenty routes") is satisfied by a sweep that has
    /// quietly lost eight of them, and says nothing about a route that appeared and was never looked
    /// at. <see cref="TenantSweepShape"/> names every route in each class, and the failure names
    /// exactly which one came or went — including the burst of names a single group-level
    /// <c>ExemptFromTenantSweep</c> would produce.
    /// </summary>
    [Fact]
    public async Task Sweep_Reports_What_It_Covered()
    {
        var sweep = await SweepAsync();
        var resource = ResourceEndpoints(sweep).ToList();
        var exempt = sweep.Endpoints.Where(e => e.IsExempt).ToList();
        var collections = sweep.Endpoints
            .Where(e => e.Class == SweepClass.Collection && e.IsVersionedApi && !e.IsExempt).ToList();

        var culture = CultureInfo.InvariantCulture;
        var report = new System.Text.StringBuilder()
            .AppendLine(culture, $"endpoints discovered : {sweep.Endpoints.Count}")
            .AppendLine(culture, $"resource endpoints   : {resource.Count}")
            .AppendLine(culture, $"  swept (tenant)     : {resource.Count(e => !e.IsExempt && !e.IsRootOnly)}")
            .AppendLine(culture, $"  root-only          : {resource.Count(e => !e.IsExempt && e.IsRootOnly)}")
            .AppendLine(culture, $"  exempt             : {resource.Count(e => e.IsExempt)}")
            .AppendLine(culture, $"list endpoints swept : {collections.Count}")
            .AppendLine(culture, $"other (no id, write) : {sweep.Endpoints.Count(e => e.Class == SweepClass.Other)}")
            .AppendLine("exempt routes:");

        foreach (var e in exempt.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            report.AppendLine(culture, $"  {e.Name}\n      {e.ExemptReason}");
        }

        _output.WriteLine(report.ToString());

        AssertShape(
            "resource routes swept with the other tenant's id",
            resource.Where(e => !e.IsExempt && !e.IsRootOnly).Select(e => e.Name),
            TenantSweepShape.SweptResourceRoutes,
            nameof(TenantSweepShape.SweptResourceRoutes));

        AssertShape(
            "root-only resource routes",
            resource.Where(e => !e.IsExempt && e.IsRootOnly).Select(e => e.Name),
            TenantSweepShape.RootOnlyResourceRoutes,
            nameof(TenantSweepShape.RootOnlyResourceRoutes));

        AssertShape(
            "collections searched for the other tenant's rows",
            collections.Select(e => e.Name),
            TenantSweepShape.CollectionRoutes,
            nameof(TenantSweepShape.CollectionRoutes));

        AssertShape(
            "routes exempted from the sweep",
            exempt.Select(e => e.Name),
            TenantSweepShape.ExemptRoutes,
            nameof(TenantSweepShape.ExemptRoutes));
    }

    /// <summary>
    /// Compares one class of the swept surface against its pinned set, reporting the difference as
    /// the two lists an author can act on: what appeared, and what is gone.
    /// </summary>
    private static void AssertShape(
        string what, IEnumerable<string> actual, IReadOnlySet<string> expected, string setName)
    {
        var found = actual.ToHashSet(StringComparer.Ordinal);

        var drift = found.Except(expected, StringComparer.Ordinal)
            .Select(name => $"+ {name}\n      appeared: add it to {nameof(TenantSweepShape)}.{setName}")
            .Concat(expected.Except(found, StringComparer.Ordinal)
                .Select(name => $"- {name}\n      gone: it is no longer {what}; remove it from " +
                                $"{nameof(TenantSweepShape)}.{setName} if that was deliberate"))
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        drift.ShouldBeEmpty(
            $"the set of {what} is pinned route by route, so the sweep cannot shrink or grow without " +
            "somebody saying so. Update the set in the same commit as the endpoint, and let the diff " +
            $"show a reviewer that the API's tenant-scoped surface changed.\n  " +
            string.Join("\n  ", drift));
    }

    /// <summary>
    /// The query string every list probe carries. The largest page size the strictest validator on
    /// the API accepts (100 — asking for 200 made four lists answer 400 and drop out of the sweep
    /// entirely), and <c>includeInactive=true</c> so the sessions list answers with its revoked rows
    /// too — unknown query parameters are ignored by model binding, so this is safe to send
    /// everywhere.
    ///
    /// The other non-default states need no flag: the inbox shows read notifications unless asked
    /// for unread only, the user lists do not filter on active by default, and the grants list shows
    /// every status. The trash is its own endpoint. What they all need is a row of that state
    /// seeded in tenant B, which is <see cref="ResourceKind.TrashedFile"/> and its neighbours.
    /// </summary>
    private const string ListQuery = "?pageNumber=1&pageSize=100&take=100&includeInactive=true";

    #region Request shaping

    /// <summary>
    /// Non-destructive verbs first, destructive ones last, so a control that legitimately deletes a
    /// row cannot invalidate a probe that had not run yet.
    /// </summary>
    private static IEnumerable<SweptEndpoint> Ordered(IEnumerable<SweptEndpoint> endpoints) =>
        endpoints.OrderBy(IsDestructive).ThenBy(e => e.Name, StringComparer.Ordinal);

    private static bool IsDestructive(SweptEndpoint endpoint) =>
        string.Equals(endpoint.Method, HttpMethods.Delete, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the concrete URL for an endpoint, putting <paramref name="tenant"/>'s id in every
    /// resource parameter, and returns the substituted values so a body sample can echo them.
    /// </summary>
    private static (string Path, IReadOnlyDictionary<string, string> RouteValues) Substitute(
        SweptEndpoint endpoint,
        SeededTenant tenant,
        IReadOnlyDictionary<ResourceKind, string>? overrides = null)
    {
        var path = Materialise(endpoint.Template);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parameter in endpoint.ResourceParameters)
        {
            var kind = TenantSweepRegistry.KindFor(endpoint.Name, parameter);
            var id = overrides is not null && overrides.TryGetValue(kind, out var fresh)
                ? fresh
                : tenant[kind];

            values[parameter.Name] = id;
            path = ReplaceParameter(path, parameter.Name, id);
        }

        return (path, values);
    }

    /// <summary>Turns a route template into a callable path: version resolved, leading slash added.</summary>
    private static string Materialise(string template)
    {
        var path = template.Replace("api/v{version:apiVersion}", "api/v1", StringComparison.Ordinal);
        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>
    /// Replaces <c>{name}</c>, <c>{name:constraint}</c> or <c>{name?}</c> with a value. Written by
    /// hand rather than with a regex so the sweep cannot be defeated by an exotic constraint.
    /// </summary>
    private static string ReplaceParameter(string path, string name, string value)
    {
        var start = path.IndexOf('{' + name, StringComparison.Ordinal);
        if (start < 0)
        {
            return path;
        }

        var end = path.IndexOf('}', start);
        return end < 0 ? path : string.Concat(path.AsSpan(0, start), value, path.AsSpan(end + 1));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        SweptEndpoint endpoint,
        string path,
        IReadOnlyDictionary<string, string> routeValues,
        SeededTenant caller)
    {
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), path);

        var body = TenantSweepBodies.For(endpoint.Method, endpoint.Template, routeValues, caller);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await client.SendAsync(request);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Rows created for a single destructive control, so the control does not consume a row the rest
    /// of the sweep still needs. Null means no factory could produce them, which the caller reports
    /// as a failure rather than a skip.
    /// </summary>
    private static Task<IReadOnlyDictionary<ResourceKind, string>?> MintFreshAsync(
        TenantSweepFixture sweep, SweptEndpoint endpoint)
    {
        var kinds = endpoint.ResourceParameters
            .Select(p => TenantSweepRegistry.KindFor(endpoint.Name, p))
            .Distinct()
            .ToList();

        return TenantSweepFreshRows.TryCreateAsync(sweep, sweep.A, endpoint.Name, kinds);
    }

    /// <summary>
    /// Read-back probes for the "tenant B is untouched" assertion: one GET per seeded row, chosen so
    /// the check fails if the row was deleted rather than merely hidden. Each probe names the id it
    /// must still find, so a row that vanished from a list is caught as surely as one whose GET 404s.
    ///
    /// The last four are lists because the rows have no by-id route of their own: a session, a
    /// notification, an impersonation grant and a file in the trash are each read back through the
    /// collection that shows them. The trash probe is the one that answers the restore route — a
    /// cross-tenant restore that worked would take the file out of that list.
    /// </summary>
    private static IEnumerable<(ResourceKind Kind, string Path, string MustContain)> ReadBackPaths(
        SeededTenant tenant)
    {
        yield return (ResourceKind.User,
            $"/api/v1/identity/users/{tenant[ResourceKind.User]}", tenant[ResourceKind.User]);
        yield return (ResourceKind.Role,
            $"/api/v1/identity/roles/{tenant[ResourceKind.Role]}", tenant[ResourceKind.Role]);
        yield return (ResourceKind.Group,
            $"/api/v1/identity/groups/{tenant[ResourceKind.Group]}", tenant[ResourceKind.Group]);
        yield return (ResourceKind.File,
            $"/api/v1/files/{tenant[ResourceKind.File]}", tenant[ResourceKind.File]);
        yield return (ResourceKind.Audit,
            $"/api/v1/audits/{tenant[ResourceKind.Audit]}", tenant[ResourceKind.Audit]);

        // includeInactive is left off on purpose: the sessions list then shows live sessions only,
        // so finding the id there proves the session was not revoked, not merely that the row exists.
        yield return (ResourceKind.Session,
            "/api/v1/identity/sessions?pageNumber=1&pageSize=100", tenant[ResourceKind.Session]);
        yield return (ResourceKind.Notification,
            "/api/v1/notifications?page=1&pageSize=100", tenant[ResourceKind.Notification]);
        yield return (ResourceKind.ImpersonationGrant,
            "/api/v1/identity/impersonation/grants?take=100", tenant[ResourceKind.ImpersonationGrant]);
        yield return (ResourceKind.TrashedFile,
            "/api/v1/files/trash?pageNumber=1&pageSize=100", tenant[ResourceKind.TrashedFile]);
    }

    private static string Truncate(string body) =>
        body.Length <= 400 ? body : body[..400] + "…";

    /// <summary>
    /// Everything that identifies a row of <paramref name="tenant"/> in a response body: its marker,
    /// its admin's e-mail, its id, and the id of every row the sweep seeded in it.
    /// </summary>
    private static List<string> NeedlesFor(SeededTenant tenant)
    {
        var needles = new List<string> { tenant.Marker, tenant.AdminEmail, tenant.TenantId };
        needles.AddRange(tenant.Ids
            .Where(pair => pair.Key != ResourceKind.Tenant)
            .Select(pair => pair.Value));
        return needles;
    }

    /// <summary>
    /// True when every resource this route names is platform-wide by construction, so "root must 404
    /// too" is not a question that applies to it.
    /// </summary>
    private static bool AddressesPlatformWideRow(SweptEndpoint endpoint) =>
        endpoint.ResourceParameters.Count > 0
        && endpoint.ResourceParameters.All(p =>
            TenantSweepExceptions.PlatformWideKinds.Contains(TenantSweepRegistry.KindFor(endpoint.Name, p)));

    private static bool IsCollectionShaped(SweptEndpoint endpoint) =>
        TenantSweepExceptions.CollectionShapedRoutes.ContainsKey($"{endpoint.Method} {endpoint.Template}");

    /// <summary>
    /// The verdict for one probe of <paramref name="other"/>'s id: null when the endpoint behaved,
    /// otherwise the line to print. 404 is the rule; a collection-shaped route may answer 200 instead,
    /// but then its body has to be empty of the other tenant.
    /// </summary>
    private static async Task<string?> JudgeAsync(
        SweptEndpoint endpoint, HttpResponseMessage response, SeededTenant other)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (IsCollectionShaped(endpoint))
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                return $"      expected an empty result or 404, got {(int)response.StatusCode} " +
                       $"{response.StatusCode}\n      body: {Truncate(body)}";
            }

            var hits = NeedlesFor(other)
                .Where(needle => body.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // A non-zero count on a bulk route means it reached rows it should not have.
            bool touchedRows = body.Contains("revokedCount", StringComparison.Ordinal)
                && !body.Contains("\"revokedCount\":0", StringComparison.Ordinal);

            if (hits.Count == 0 && !touchedRows)
            {
                return null;
            }

            return $"      answered {(int)response.StatusCode} carrying the other tenant's data " +
                   $"({string.Join(", ", hits)})\n      body: {Truncate(body)}";
        }

        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : $"      expected 404, got {(int)response.StatusCode} {response.StatusCode}\n" +
              $"      body: {Truncate(body)}";
    }

    #endregion
}
