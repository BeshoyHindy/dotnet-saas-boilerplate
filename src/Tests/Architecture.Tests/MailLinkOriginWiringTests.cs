using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every local stack tells the API which origin to write into password-reset and
/// email-confirmation links, in a product with clients and in one without.
///
/// <para>The Identity module never guesses that origin from the request (a swapped <c>Host</c>
/// header in a mailed link is an account-takeover primitive), and with none configured it refuses to
/// send the mail at all (<c>MailLinkOrigin.Require</c>, pinned by the handler tests). So a stack that
/// forgets the setting does not mail a broken link — it fails every reset and confirmation. With the
/// clients the origin is the dashboard's; a scaffold made with <c>--frontend false</c> has no client
/// page to link to and uses the API's own origin instead, rather than a dashboard nothing serves.</para>
///
/// <para>Whether this tree ships clients is read off the tree itself, because the template's own
/// source carries the lines of both variants. The Dokploy stack is pinned the same way by
/// <c>deploy/dokploy/tests/compose-contract.test.sh</c>. Text scans: no Docker needed.</para>
/// </summary>
public sealed class MailLinkOriginWiringTests
{
    private const string OriginKey = "OriginOptions__OriginUrl";

    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    private static bool ShipsClients => Directory.Exists(Path.Combine(SolutionRoot, "clients/dashboard"));

//#if (aspire)
    private const string AppHostPath = "src/Host/Boilerplate.AppHost/AppHost.cs";
    private const string ApiOwnOrigin = $"api.WithEnvironment(\"{OriginKey}\", api.GetEndpoint(\"https\"));";
    private const string DashboardOrigin = $"api.WithEnvironment(\"{OriginKey}\", DashboardOrigin);";
//#endif
    private const string LocalComposePath = "docker-compose.yml";

    #region Happy Path

    // The AppHost is dropped from a scaffold made with --aspire false, and its checks with it.
//#if (aspire)
    [Fact]
    public void AppHost_Should_SetTheMailLinkOrigin_When_ClientsShipOrNot()
    {
        string appHost = Read(AppHostPath);

        if (ShipsClients)
        {
            appHost.ShouldContain(DashboardOrigin, customMessage: "mailed links must point at the dashboard");
        }
        else
        {
            appHost.ShouldContain(ApiOwnOrigin, customMessage: "without a client, mailed links carry the API's own origin");
            appHost.ShouldNotContain(DashboardOrigin, customMessage: "no dashboard ships, so none may be linked to");
        }
    }

    [Fact]
    public void AppHost_Should_SetTheDashboardOriginLast_When_BothVariantsShareOneTree()
    {
        // The template's markers are plain C# comments, so its own tree runs both statements and
        // the later one wins. Swapping them would silently point the template's mails at the API.
        string appHost = Read(AppHostPath);
        int apiOwn = appHost.IndexOf(ApiOwnOrigin, StringComparison.Ordinal);
        int dashboard = appHost.IndexOf(DashboardOrigin, StringComparison.Ordinal);

        if (apiOwn >= 0 && dashboard >= 0)
        {
            dashboard.ShouldBeGreaterThan(apiOwn, "the dashboard origin must be set after the API's own");
        }
    }
//#endif

    [Fact]
    public void LocalCompose_Should_SetTheMailLinkOriginOnce_When_ClientsShipOrNot()
    {
        string[] originLines = Read(LocalComposePath)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith($"{OriginKey}:", StringComparison.Ordinal))
            .ToArray();

        originLines.Length.ShouldBe(1, "a YAML map holds a key once; the variants differ by value, not by key");
        originLines[0].ShouldBe(ShipsClients
            ? $"{OriginKey}: ${{APP_DASHBOARD_URL:-http://localhost:8081}}"
            : $"{OriginKey}: http://localhost:${{APP_API_PORT:-8080}}");
    }

    #endregion

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(SolutionRoot, relativePath));
}
