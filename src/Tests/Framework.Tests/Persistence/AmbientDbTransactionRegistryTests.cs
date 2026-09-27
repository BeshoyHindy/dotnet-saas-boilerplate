using Boilerplate.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;
using Xunit;

namespace Framework.Tests.Persistence;

/// <summary>
/// Guards the silent-no-op failure mode of <see cref="AmbientDbTransactionRegistry"/>.
///
/// <see cref="IDbTransactionInterceptor"/> gives every member a default no-op implementation, so a
/// registry method whose signature does not match the interface still compiles and is simply never
/// invoked. The registry then stays empty, the outbox never enlists in the business transaction,
/// and the transactional-outbox guarantee is silently lost — which PostgreSQL masks, because Npgsql
/// associates commands with the connection's open transaction regardless.
/// </summary>
public class AmbientDbTransactionRegistryTests
{
    private static readonly string[] MustBeImplemented =
    [
        "TransactionStarted",
        "TransactionStartedAsync",
        "TransactionUsed",
        "TransactionUsedAsync",
        "TransactionCommitted",
        "TransactionCommittedAsync",
        "TransactionRolledBack",
        "TransactionRolledBackAsync",
        "TransactionFailed",
        "TransactionFailedAsync"
    ];

    #region Happy Path

    [Fact]
    public void Registry_Should_Implement_Every_Interceptor_Hook_It_Relies_On()
    {
        var map = typeof(AmbientDbTransactionRegistry).GetInterfaceMap(typeof(IDbTransactionInterceptor));

        var notWiredUp = new List<string>();
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            var name = map.InterfaceMethods[i].Name;
            if (!MustBeImplemented.Contains(name))
            {
                continue;
            }

            // When a signature does not match, the interface's own default implementation is the
            // target — meaning the registry's method is dead code.
            if (map.TargetMethods[i].DeclaringType != typeof(AmbientDbTransactionRegistry))
            {
                notWiredUp.Add(name);
            }
        }

        notWiredUp.ShouldBeEmpty(
            "these IDbTransactionInterceptor hooks fall through to the interface default, so the "
            + "registry never records transactions started via them: " + string.Join(", ", notWiredUp));
    }

    #endregion
}
