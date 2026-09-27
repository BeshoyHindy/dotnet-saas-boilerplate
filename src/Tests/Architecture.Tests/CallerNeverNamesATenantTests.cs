using Shouldly;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// ADR-0002's first rule, <i>a caller never names a tenant</i>, as a fast test (no Docker).
///
/// <para>The scaffold dry run (#92, F36) added <c>string? TenantId</c> to a create command and had the
/// handler write it onto the new row. The build, every architecture test and the cross-tenant sweep
/// stayed green — the sweep substitutes route ids and cannot see a body field — and the field would
/// have shipped in the Contract. Only Finbuckle's save-time mismatch check stopped the write. This
/// test closes that gap at the Contract: a command or query an endpoint binds or builds may not carry
/// a property whose name says "tenant", unless it is listed below with the reason it is not the
/// caller choosing its own tenant.</para>
///
/// <para><b>How it finds the requests.</b> Every <c>…Command</c>/<c>…Query</c> type name that appears
/// in a module's <c>*Endpoint.cs</c> file, resolved against the Contracts assemblies — so a request
/// dispatched only by a job or an event handler, where the tenant comes from the ambient scope, is not
/// asked about. Nested contract types (a DTO inside a command) are walked too.</para>
/// </summary>
public sealed partial class CallerNeverNamesATenantTests
{
    /// <summary>
    /// Properties allowed to name a tenant, keyed <c>RequestType.Property</c>, and why. Every entry is
    /// either an operator acting <i>on</i> a tenant (the tenant is the resource, and the permission is
    /// root-only) or the one sanctioned anonymous input, the <c>{tenant}</c> route segment. Adding an
    /// entry is a design decision a reviewer must agree with; it is never how a tenant-scoped feature
    /// learns which tenant it is in — that is the token's <c>tenant</c> claim, already ambient.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // The tenant catalog: root operators administer a tenant, so the tenant is the resource the
        // route addresses. Every one of these is behind a Tenants.* permission flagged IsRoot.
        ["AdjustTenantValidityCommand.TenantId"] = "root-only tenant administration; the tenant is the resource",
        ["ChangeTenantActivationCommand.TenantId"] = "root-only tenant administration; the tenant is the resource",
        ["GetTenantProvisioningStatusQuery.TenantId"] = "root-only tenant administration; the tenant is the resource",
        ["GetTenantStatusQuery.TenantId"] = "root-only tenant administration; the tenant is the resource",
        ["RenewTenantCommand.TenantId"] = "root-only tenant administration; the tenant is the resource",
        ["RetryTenantProvisioningCommand.TenantId"] = "root-only tenant administration; the tenant is the resource",

        // The one sanctioned anonymous input (ADR-0002): the {tenant} route segment of the auth routes.
        ["ConfirmEmailCommand.Tenant"] =
            "anonymous auth route; bound from the {tenant} route segment ADR-0002 sanctions",

        // Operators crossing tenants, the ADR-0002 way.
        ["ExchangeOperatorTokenCommand.TargetTenantId"] =
            "the operator token exchange: root-only (Platform.CrossTenantImpersonate), audited, and it " +
            "returns a token for the target tenant rather than acting in it",
        ["StartImpersonationCommand.TargetTenantId"] =
            "must equal the caller's own tenant — the handler refuses any other with 403 and points at " +
            "the operator token exchange",

        // Cross-tenant reads for root, re-pinned to the one requested tenant.
        ["GetAuditsQuery.TenantId"] =
            "a different tenant than the caller's requires AuditTrails.ViewCrossTenant (IsRoot); the " +
            "handler re-pins the query to that one tenant",
        ["GetAuditSummaryQuery.TenantId"] =
            "a different tenant than the caller's requires AuditTrails.ViewCrossTenant (IsRoot); the " +
            "handler re-pins the query to that one tenant",
        ["GetImpersonationGrantsQuery.ImpersonatedTenantId"] =
            "honoured for root only; for anyone else the handler filters by the caller's own tenant",
    };

    [GeneratedRegex(@"\b([A-Z]\w*(?:Command|Query))\b", RegexOptions.CultureInvariant)]
    private static partial Regex RequestTypeName();

    [Fact]
    public void No_Request_An_Endpoint_Binds_Should_Name_A_Tenant()
    {
        var requests = EndpointRequestTypes();

        // Anti-vacuous: a scan that stopped resolving requests would pass by looking at nothing.
        requests.Count.ShouldBeGreaterThan(40, "the scan should resolve every endpoint's command and query.");

        var offenders = TenantNamingProperties(requests)
            .Where(path => !Allowed.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "ADR-0002: a caller never names a tenant. The tenant of a request is the token's `tenant` " +
            "claim, already ambient in the handler — a tenant id in a command or query is a second, " +
            "caller-controlled answer to the same question. Remove the property and let the tenant " +
            "context decide. If the tenant is the resource an operator acts on (a root-only " +
            $"permission), add the property to {nameof(CallerNeverNamesATenantTests)}.{nameof(Allowed)} " +
            "with the reason. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_Allowed_Entry_Should_Still_Exist()
    {
        var found = TenantNamingProperties(EndpointRequestTypes()).ToHashSet(StringComparer.Ordinal);

        var stale = Allowed.Keys
            .Where(key => !found.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            "these allow-list entries match no property an endpoint's request carries any more. Delete " +
            "them, so the list stays a list of decisions still in force:\n  " + string.Join("\n  ", stale));
    }

    /// <summary><c>Type.Property</c> (or a dotted path into a nested contract type) for every tenant-named property.</summary>
    private static IEnumerable<string> TenantNamingProperties(IEnumerable<Type> requests)
    {
        var found = new List<string>();
        foreach (var request in requests)
        {
            Inspect(request, request.Name, depth: 0, found);
        }

        return found.Distinct(StringComparer.Ordinal);
    }

    private static void Inspect(Type type, string path, int depth, List<string> found)
    {
        if (depth > 3)
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var here = $"{path}.{property.Name}";

            if (property.Name.Contains("tenant", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(here);
                continue;
            }

            var owned = OwnedContractType(property.PropertyType);
            if (owned is not null)
            {
                Inspect(owned, here, depth + 1, found);
            }
        }
    }

    /// <summary>
    /// The contract type worth descending into — the property's own type, or the element type of a
    /// collection of them — or null for primitives, strings and framework types.
    /// </summary>
    private static Type? OwnedContractType(Type type)
    {
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            type = type.IsArray
                ? type.GetElementType()!
                : type.GetGenericArguments().LastOrDefault() ?? type;
        }

        return type.Namespace?.StartsWith("Boilerplate.", StringComparison.Ordinal) == true
               && !type.IsEnum
               && type != typeof(string)
            ? type
            : null;
    }

    /// <summary>
    /// Every command and query named in a module endpoint file, resolved in the Contracts assemblies.
    /// Whole-file text rather than route chains: an endpoint often binds its request in a separate
    /// handler method the chain only names as a method group.
    /// </summary>
    private static List<Type> EndpointRequestTypes()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in IdempotencyFilterOrderTests.EndpointSourceFiles())
        {
            foreach (Match match in RequestTypeName().Matches(File.ReadAllText(file)))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return IdempotentCommandSecretsTests.ContractsAssemblies()
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => names.Contains(t.Name))
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }
}
