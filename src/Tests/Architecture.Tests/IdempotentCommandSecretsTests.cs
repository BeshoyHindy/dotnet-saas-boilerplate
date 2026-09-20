using Boilerplate.BuildingBlocks.Shared.Security;
using Shouldly;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// A request bound by an idempotent endpoint is hashed into a cache entry that outlives it by up to
/// 24 hours, so every field that reaches the hash is a field an attacker who reaches Redis can grind
/// a candidate list against. <c>IdempotencyEndpointFilter</c> drops anything
/// <see cref="SensitiveFieldNames"/> recognises or <c>[NotFingerprinted]</c> marks; this test asks
/// the other half of the question — whether a command actually binds something secret-shaped that
/// neither of those catches.
///
/// <para>It exists because that is exactly what happened: <c>CreateTenantCommand.ConnectionString</c>
/// is a database credential, the name list did not know the word, and nothing failed. The probe below
/// is deliberately <i>wider</i> than the production rule — it flags "connection", "salt", "private"
/// and friends that <see cref="SensitiveFieldNames"/> does not — so a new command has to be looked at
/// rather than silently accepted.</para>
///
/// <para><b>How it finds the commands.</b> The route chains carrying <c>.WithIdempotency()</c>
/// (<see cref="RouteChains"/>), the <c>…Command</c> type names bound in each, resolved against the
/// Contracts assemblies. It fails if it resolves nothing, so a rename cannot turn it into a test that
/// passes by looking at nothing.</para>
/// </summary>
public sealed partial class IdempotentCommandSecretsTests
{
    /// <summary>
    /// Words that make a field worth a second look, beyond the ones the filter already drops. A hit
    /// here is not a verdict — it is "this must be excluded, or explained".
    /// </summary>
    private static readonly string[] SecretShapedWords =
    [
        "password", "passphrase", "secret", "token", "credential", "connection",
        "apikey", "privatekey", "certificate", "salt", "hash", "signature", "otp", "pin",
    ];

    [GeneratedRegex(@"\b([A-Z]\w*Command)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CommandTypeName();

    [Fact]
    public void A_Command_Bound_By_An_Idempotent_Endpoint_Should_Not_Fingerprint_A_Secret()
    {
        var commands = IdempotentCommandTypes();

        commands.ShouldNotBeEmpty(
            "this test resolves the commands from the endpoint sources; finding none means the scan " +
            "broke, not that the kit stopped binding commands.");

        var offenders = new List<string>();
        foreach (var command in commands)
        {
            Inspect(command, command.Name, depth: 0, offenders);
        }

        offenders.ShouldBeEmpty(
            "a secret-shaped field on an idempotent endpoint's command is hashed into an entry that " +
            "outlives the request. Add the word to SensitiveFieldNames if it belongs there for every " +
            "consumer, and mark the property [NotFingerprinted] to say so at the property. " +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_Sweep_Should_Cover_Every_Idempotent_Endpoint()
    {
        // Named rather than counted so the failure says which one moved.
        IdempotentCommandTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["CreateTenantCommand", "RegisterUserCommand", "RequestUploadUrlCommand"]);
    }

    [Fact]
    public void The_Probe_Should_Recognise_The_Field_That_Got_Through()
    {
        // The regression itself: ConnectionString is secret-shaped, and before this branch the
        // production rule did not know it. Both halves must hold now.
        LooksSecret("ConnectionString").ShouldBeTrue();
        SensitiveFieldNames.IsSensitive("ConnectionString").ShouldBeTrue();

        var connectionString = typeof(Boilerplate.Modules.Multitenancy.Contracts.v1.CreateTenant.CreateTenantCommand)
            .GetProperty("ConnectionString");

        connectionString.ShouldNotBeNull();
        connectionString.IsDefined(typeof(NotFingerprintedAttribute), inherit: true).ShouldBeTrue();
    }

    /// <summary>
    /// Walks a command's properties, and the properties of any type it owns, flagging a secret-shaped
    /// name that is neither dropped by name nor marked. Depth-limited and cycle-free: a contract DTO
    /// graph is shallow, and following it forever would be a different test.
    /// </summary>
    private static void Inspect(Type type, string path, int depth, List<string> offenders)
    {
        if (depth > 3)
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var here = $"{path}.{property.Name}";

            if (SensitiveFieldNames.IsSensitive(property.Name)
                || property.IsDefined(typeof(NotFingerprintedAttribute), inherit: true))
            {
                continue;
            }

            if (LooksSecret(property.Name))
            {
                offenders.Add(here);
                continue;
            }

            if (IsOwnedContractType(property.PropertyType))
            {
                Inspect(property.PropertyType, here, depth + 1, offenders);
            }
        }
    }

    private static bool LooksSecret(string name) =>
        Array.Exists(SecretShapedWords, w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>A nested contract type worth descending into — not a primitive, a string or a framework type.</summary>
    private static bool IsOwnedContractType(Type type) =>
        type is { IsClass: true } or { IsValueType: true, IsPrimitive: false, IsEnum: false }
        && type != typeof(string)
        && type.Namespace?.StartsWith("Boilerplate.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Every command type bound by a route chain that carries <c>.WithIdempotency()</c>, resolved in
    /// the assemblies the module contracts live in.
    /// </summary>
    private static List<Type> IdempotentCommandTypes()
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in IdempotencyFilterOrderTests.EndpointSourceFiles())
        {
            foreach (var chain in RouteChains.Split(File.ReadAllText(file)))
            {
                if (!chain.Contains(".WithIdempotency", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match name in CommandTypeName().Matches(chain))
                {
                    candidates.Add(name.Groups[1].Value);
                }
            }
        }

        return ContractsAssemblies()
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => candidates.Contains(t.Name))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The Contracts assemblies beside the test binary, loaded by file the way
    /// <c>ModuleAssemblyDiscovery</c> does — "whatever happens to be loaded already" depends on which
    /// test ran first, which is not a thing to build a guarantee on.
    /// </summary>
    private static IEnumerable<Assembly> ContractsAssemblies()
    {
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "Boilerplate.Modules.*.Contracts.dll"))
        {
            Assembly? assembly = null;
            try
            {
                assembly = Assembly.Load(AssemblyName.GetAssemblyName(file));
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception)
            {
                // Not a managed assembly, or unloadable here. The emptiness check above is the guard.
            }
#pragma warning restore CA1031

            if (assembly is not null)
            {
                yield return assembly;
            }
        }
    }
}
