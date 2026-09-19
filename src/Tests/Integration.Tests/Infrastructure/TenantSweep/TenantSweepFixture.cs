namespace Integration.Tests.Infrastructure.TenantSweep;

/// <summary>
/// The arrange step of the whole sweep, done once: two real, fully provisioned tenants — A and B —
/// each carrying one live row of every resource kind the API addresses by id, and each with an admin
/// client.
///
/// Tenant admins are used rather than the root admin on purpose. A provisioned tenant's Admin role
/// is synced with <i>every</i> non-root permission (see <c>RolePermissionSyncer</c>), so when the
/// sweep gets a 403 it cannot be because the caller was short a permission — the only remaining
/// explanations are a root-only route or a genuine authorization-before-lookup bug, and the sweep
/// tells those apart from permission metadata.
///
/// <para>Built lazily behind <see cref="GetAsync"/> rather than as an xUnit collection fixture:
/// provisioning two tenants and seeding a dozen rows costs about a minute, and a collection fixture
/// cannot take <see cref="AppWebApplicationFactory"/> (itself a collection fixture) as a constructor
/// argument. The sweep therefore joins the suite's existing collection — sharing the one
/// Testcontainers host instead of starting a second — and builds this on first use.</para>
///
/// Tenants get a unique id per run and every seeded row carries a per-tenant marker, so nothing here
/// collides with the rest of the suite.
/// </summary>
public sealed class TenantSweepFixture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TenantSweepFixture? _instance;

    private TenantSweepFixture(AppWebApplicationFactory factory)
    {
        Factory = factory;
        Auth = new AuthHelper(factory);
    }

    private AppWebApplicationFactory Factory { get; }

    public AuthHelper Auth { get; }

    /// <summary>The tenant whose token the sweep calls with.</summary>
    public SeededTenant A { get; private set; } = default!;

    /// <summary>The tenant whose ids and rows must stay invisible to A.</summary>
    public SeededTenant B { get; private set; } = default!;

    /// <summary>Root operator client — root must get 404 for B's tenant-scoped rows too (ADR-0002).</summary>
    public HttpClient RootClient { get; private set; } = default!;

    /// <summary>Every route the running host published, classified.</summary>
    public IReadOnlyList<SweptEndpoint> Endpoints { get; private set; } = [];

    /// <summary>
    /// The shared, once-per-run sweep state. Every test in the class awaits this; the semaphore makes
    /// the first one pay for provisioning and the rest read the result.
    /// </summary>
    public static async Task<TenantSweepFixture> GetAsync(AppWebApplicationFactory factory)
    {
        await Gate.WaitAsync();
        try
        {
            _instance ??= await BuildAsync(factory);
            return _instance;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<TenantSweepFixture> BuildAsync(AppWebApplicationFactory factory)
    {
        var sweep = new TenantSweepFixture(factory)
        {
            Endpoints = EndpointSweepDiscovery.Discover(factory.Services),
        };

        sweep.RootClient = await sweep.Auth.CreateRootAdminClientAsync();

        var tenants = new TenantFixtures(factory);

        // Sequential, not parallel: provisioning runs migrations behind an advisory lock, and two
        // tenants racing it only makes the fixture slower and flakier.
        sweep.A = await sweep.SeedTenantAsync(tenants, "sweepa");
        sweep.B = await sweep.SeedTenantAsync(tenants, "sweepb");

        return sweep;
    }

    private async Task<SeededTenant> SeedTenantAsync(TenantFixtures tenants, string prefix)
    {
        var (tenantId, adminEmail) = await tenants.CreateProvisionedTenantAsync(prefix);

        // Token issuance races the seeding step of provisioning; TenantFixture retries for us.
        var token = await TenantFixture.GetTokenWithRetryAsync(
            Auth, adminEmail, TestConstants.DefaultPassword, tenantId);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // A short, searchable, tenant-unique token. It goes into every seeded row's display fields,
        // which is what lets the list half of the sweep spot a leak on any endpoint at all.
        var marker = $"{prefix}{tenantId[^8..]}";

        var ids = await TenantSweepSeeder.SeedAsync(Factory, Auth, client, tenantId, marker);

        return new SeededTenant
        {
            TenantId = tenantId,
            AdminEmail = adminEmail,
            Marker = marker,
            AdminClient = client,
            Ids = ids,
        };
    }
}
