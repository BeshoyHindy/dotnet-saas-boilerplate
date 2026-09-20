using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.Idempotency;

/// <summary>
/// Idempotent replay against a <b>real</b> distributed cache — the thing issue #82 says was never
/// true. The filter used to write through HybridCache and probe with <c>IDistributedCache</c>, so
/// the probe read HybridCache's framed L2 payload as if it were JSON: 500 on the first replay
/// against Redis, and nothing at all with the in-memory fallback, which HybridCache refuses to use
/// as an L2. Neither failure is observable without an actual Redis behind the filter, which is what
/// this class stands up.
/// </summary>
/// <remarks>
/// A purpose-built host rather than <c>AppWebApplicationFactory</c>: that host runs without Redis on
/// purpose, and pointing it at one would put a Valkey container in front of every integration test
/// for the sake of this file. What is real here is what matters — the real filter, the real
/// <c>AddHeroCaching</c> registrations, real tenant scoping, and a real Valkey container (the same
/// image <see cref="Caching.HybridCacheRedisTests"/> uses).
/// </remarks>
public sealed class IdempotencyRedisReplayTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Tenant";
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string ReplayedHeader = "Idempotency-Replayed";

    private readonly RedisContainer _redis = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private int _runs;

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task SameKey_Twice_Should_Replay_The_First_Response_And_Run_TheHandler_Once()
    {
        await using var app = await StartHostAsync();
        using var client = app.GetTestClient();

        var first = await PostAsync(client, "acme", "key-1", "widget");
        var second = await PostAsync(client, "acme", "key-1", "widget");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        first.Headers.Contains(ReplayedHeader).ShouldBeFalse();

        second.StatusCode.ShouldBe(HttpStatusCode.Created,
            "the replay used to be a 500 here — the probe deserialized HybridCache's framed payload.");
        second.Headers.Contains(ReplayedHeader).ShouldBeTrue();
        (await second.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());
        second.Headers.Location?.ToString().ShouldBe(first.Headers.Location?.ToString());

        _runs.ShouldBe(1, "the handler must run exactly once for one Idempotency-Key.");
    }

    [Fact]
    public async Task SameKey_In_TwoTenants_Should_Not_Share_A_Replay()
    {
        await using var app = await StartHostAsync();
        using var client = app.GetTestClient();

        var first = await PostAsync(client, "acme", "shared-key", "widget");
        var second = await PostAsync(client, "globex", "shared-key", "widget");

        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.Contains(ReplayedHeader).ShouldBeFalse(
            "one tenant's Idempotency-Key must never reach another tenant's entry.");
        (await second.Content.ReadAsStringAsync()).ShouldNotBe(await first.Content.ReadAsStringAsync());

        _runs.ShouldBe(2);

        // Both entries exist, each under its own tenant namespace.
        (await KeysAsync(app, "t:acme:idem:*")).ShouldHaveSingleItem();
        (await KeysAsync(app, "t:globex:idem:*")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_Unreadable_Entry_In_Redis_Should_Be_A_Miss_Not_A_500()
    {
        await using var app = await StartHostAsync();
        using var client = app.GetTestClient();

        await PostAsync(client, "acme", "key-1", "widget");

        // Exactly the bytes the old write path left behind: a HybridCache-framed payload under the
        // key the filter reads. It must run the handler again, not throw on the way in.
        var physicalKey = (await KeysAsync(app, "t:acme:idem:*")).Single();

        var l2 = app.Services.GetRequiredService<IDistributedCache>();
        await l2.SetAsync(
            physicalKey,
            [0x03, 0x01, 0x7B, 0x00, 0xFF],
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

        var afterPoison = await PostAsync(client, "acme", "key-1", "widget");

        afterPoison.StatusCode.ShouldBe(HttpStatusCode.Created);
        afterPoison.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        _runs.ShouldBe(2);

        // The overwrite is readable again, so the miss is self-healing rather than permanent.
        var afterRecovery = await PostAsync(client, "acme", "key-1", "widget");
        afterRecovery.Headers.Contains(ReplayedHeader).ShouldBeTrue();
        _runs.ShouldBe(2);
    }

    /// <summary>Every physical key in Redis matching <paramref name="pattern"/> — the real key space, scanned.</summary>
    private static async Task<List<string>> KeysAsync(WebApplication app, string pattern)
    {
        var multiplexer = app.Services.GetRequiredService<IConnectionMultiplexer>();
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);

        var keys = new List<string>();
        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            keys.Add(key.ToString());
        }

        return keys;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string tenant, string key, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/things")
        {
            Content = JsonContent.Create(new ThingRequest(name))
        };
        request.Headers.Add(TenantHeader, tenant);
        request.Headers.Add(IdempotencyHeader, key);
        return await client.SendAsync(request);
    }

    private async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CachingOptions:Redis"] = _redis.GetConnectionString(),
        });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ICacheTenantAccessor, HeaderTenantAccessor>();
        builder.Services.AddHeroCaching(builder.Configuration);
        builder.Services.AddHeroIdempotency(builder.Configuration);

        var app = builder.Build();
        app.MapPost("/things", (ThingRequest request) =>
            {
                var id = Interlocked.Increment(ref _runs);
                return TypedResults.Created($"/things/{id}", new ThingResponse(id, request.Name));
            })
            .WithIdempotency();

        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// Stands in for the Multitenancy module's Finbuckle-backed accessor: the tenant comes off the
    /// request, so a test can drive two tenants through one host the way the real one does.
    /// </summary>
    private sealed class HeaderTenantAccessor(IHttpContextAccessor accessor) : ICacheTenantAccessor
    {
        public string? TenantId
        {
            get
            {
                var tenant = accessor.HttpContext?.Request.Headers[TenantHeader].ToString();
                return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
            }
        }
    }

    private sealed record ThingRequest(string Name);

    private sealed record ThingResponse(int Id, string Name);
}
