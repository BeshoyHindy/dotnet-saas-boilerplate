namespace Integration.Tests.Infrastructure;

public static class TestConstants
{
    public const string RootTenantId = "root";
    public const string RootAdminEmail = "admin@root.com";
    public const string DefaultPassword = "123Pa$$word!";

    public const string JwtIssuer = "boilerplate";
    public const string JwtAudience = "boilerplate.clients";
    public const string JwtSigningKey = "integration-test-signing-key-that-is-at-least-32-chars-long!!";

    /// <summary>
    /// Ceiling the test host configures for acting tokens (operator exchange + impersonation).
    /// Deliberately below the shipped default so the server-side clamp is observable.
    /// </summary>
    public const int OperatorExchangeMaxMinutes = 20;

    public const string IdentityBasePath = "/api/v1/identity";
    public const string TenantsBasePath = "/api/v1/tenants";
    public const string AuditsBasePath = "/api/v1/audits";

    /// <summary>
    /// The anonymous, tenant-scoped auth routes. The tenant travels in the path because no
    /// token exists yet on these calls (ADR-0002); every other call carries it in the token.
    /// </summary>
    public static string AuthBasePath(string tenant) => $"{TenantsBasePath}/{tenant}/auth";

    public static string RootAuthBasePath => AuthBasePath(RootTenantId);
}
