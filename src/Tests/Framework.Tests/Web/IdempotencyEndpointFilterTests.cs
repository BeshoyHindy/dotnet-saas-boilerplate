using System.Security.Claims;
using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Tests.Web;

/// <summary>
/// The idempotency filter is the one place in the kit that reads L2 by key, because HybridCache has
/// no get-only probe (dotnet/aspnetcore#57191). Now that the cache prefixes keys with the ambient
/// tenant, that probe has to name the <i>physical</i> key — and the failure mode if it gets it wrong
/// is silence: every replay quietly becomes a fresh execution. So these tests assert the one thing
/// that matters, directly: the key the probe asks L2 for is the key the write path stored under.
/// </summary>
/// <remarks>
/// <para>
/// Driven against the filter rather than through the integration host on purpose: that host runs
/// without Redis, and HybridCache deliberately ignores <c>MemoryDistributedCache</c> as an L2 (it
/// would only duplicate L1), so nothing about the probe is observable there at all.
/// </para>
/// <para>
/// <b>Pre-existing defect, deliberately not fixed here (issue #77 is about tenant prefixes).</b>
/// HybridCache 10.x does not write bare JSON to L2: it writes a framed payload — a version byte,
/// an expiry, the key and the tags, then the serialized value. The filter's probe deserializes those
/// bytes as plain JSON, which cannot succeed, so against a real distributed cache a replay has never
/// worked and the <c>JsonException</c> escapes the filter. It behaves identically before and after
/// this change; tests here therefore assert key agreement, which is what #77 could have broken, and
/// stop short of asserting a round-trip that the payload format prevents.
/// </para>
/// </remarks>
public sealed class IdempotencyEndpointFilterTests
{
    private const string HeaderName = "Idempotency-Key";

    private sealed class FixedTenantAccessor : ICacheTenantAccessor
    {
        public string? TenantId { get; set; }
    }

    /// <summary>
    /// A real L2 (HybridCache skips <c>MemoryDistributedCache</c>) that records the keys it is read
    /// from, so a test can assert what the probe actually asked for rather than inferring it.
    /// </summary>
    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);
        private readonly List<string> _reads = [];

        public IReadOnlyList<string> Reads
        {
            get { lock (_entries) { return [.. _reads]; } }
        }

        public IReadOnlyCollection<string> WrittenKeys
        {
            get { lock (_entries) { return [.. _entries.Keys]; } }
        }

        public byte[]? Get(string key)
        {
            lock (_entries)
            {
                _reads.Add(key);
                return _entries.TryGetValue(key, out var value) ? value : null;
            }
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            lock (_entries) { _entries[key] = value; }
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key)
        {
            lock (_entries) { _entries.Remove(key); }
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private static (ServiceProvider Provider, FixedTenantAccessor Tenant, RecordingDistributedCache L2) BuildServices()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        var tenant = new FixedTenantAccessor();
        var l2 = new RecordingDistributedCache();

        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ICacheTenantAccessor>(tenant);
        // Registered before AddHeroCaching: its in-memory fallback is a TryAdd, so this one wins.
        services.AddSingleton<IDistributedCache>(l2);
        services.AddHeroCaching(config);
        services.AddHeroIdempotency(config);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return (services.BuildServiceProvider(), tenant, l2);
    }

    private sealed class Counter
    {
        public int Executions { get; set; }
    }

    private static async Task<int> InvokeAsync(
        IServiceProvider provider,
        string idempotencyKey,
        Counter counter,
        ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Method = HttpMethods.Post;
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            httpContext.Request.Headers[HeaderName] = idempotencyKey;
        }
        httpContext.Response.Body = new MemoryStream();
        if (user is not null)
        {
            httpContext.User = user;
        }

        var filter = new IdempotencyEndpointFilter();
        await filter.InvokeAsync(new StubInvocationContext(httpContext), ctx =>
        {
            counter.Executions++;
            ctx.HttpContext.Response.StatusCode = StatusCodes.Status201Created;
            return ValueTask.FromResult<object?>(new { id = counter.Executions });
        });

        return counter.Executions;
    }

    [Fact]
    public async Task Probe_And_Write_Should_Use_The_Same_TenantPrefixed_PhysicalKey()
    {
        // The whole point of asking CacheKeyScope for the key instead of rebuilding the format in the
        // filter: if these two ever diverge, every replay silently becomes a fresh execution.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            await InvokeAsync(provider, "req-1", new Counter());

            l2.WrittenKeys.ShouldContain("t:alpha:idem:req-1");
            l2.WrittenKeys.ShouldNotContain("idem:req-1",
                "nothing may be written unprefixed — that shared bucket is what #77 removed.");

            // Second request: the probe must look under exactly the key the first one stored.
            // The call is allowed to fail while decoding what it finds — that is the pre-existing
            // payload-framing defect described on this class, and it is orthogonal to the key
            // agreement asserted here. Written tolerantly so this test keeps passing once it is fixed.
            var readsBefore = l2.Reads.Count;
            try
            {
                await InvokeAsync(provider, "req-1", new Counter());
            }
            catch (System.Text.Json.JsonException)
            {
                // Finding a JSON-undecodable entry is itself proof the probe hit the right key.
            }

            l2.Reads.Skip(readsBefore).ShouldContain("t:alpha:idem:req-1");
        }
    }

    [Fact]
    public async Task SameKey_In_TwoTenants_Should_Probe_TwoDifferentKeys_And_Both_Execute()
    {
        // Tenant B reusing tenant A's Idempotency-Key must get its own execution, never A's response.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            var counter = new Counter();

            tenant.TenantId = "alpha";
            (await InvokeAsync(provider, "shared-key", counter)).ShouldBe(1);

            tenant.TenantId = "beta";
            (await InvokeAsync(provider, "shared-key", counter)).ShouldBe(2,
                "tenant B must never replay tenant A's response.");

            l2.WrittenKeys.ShouldContain("t:alpha:idem:shared-key");
            l2.WrittenKeys.ShouldContain("t:beta:idem:shared-key");
        }
    }

    [Fact]
    public async Task No_Tenant_Should_Use_The_Global_Namespace_Not_A_Tenant_Named_Global()
    {
        // The deliberate replacement for the old `?? "global"` literal: with no tenant the entry is
        // declared global and lands in the g: namespace, rather than being filed under a tenant that
        // every tenant-less caller shares.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = null;
            await InvokeAsync(provider, "req-3", new Counter());

            l2.WrittenKeys.ShouldContain("g:idem:anon:req-3");
            l2.WrittenKeys.ShouldNotContain("t:global:idem:req-3");
            l2.Reads.ShouldContain("g:idem:anon:req-3");
        }
    }

    [Fact]
    public async Task No_Tenant_But_Authenticated_Should_Partition_By_Subject()
    {
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = null;
            var user = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-7")], "test"));

            await InvokeAsync(provider, "req-4", new Counter(), user);

            l2.WrittenKeys.ShouldContain("g:idem:s:user-7:req-4");
            l2.WrittenKeys.ShouldNotContain("g:idem:anon:req-4");
        }
    }

    [Fact]
    public async Task No_IdempotencyKey_Should_Pass_Through_Untouched_Even_With_No_Tenant()
    {
        // The filter sits on endpoints that legitimately run without the header, so it must not reach
        // the cache at all in that case — the tenant-less throw can never surface on a normal request.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = null;
            var counter = new Counter();

            (await InvokeAsync(provider, string.Empty, counter)).ShouldBe(1);
            l2.Reads.ShouldBeEmpty();
            l2.WrittenKeys.ShouldBeEmpty();
        }
    }

    /// <summary>Minimal <see cref="EndpointFilterInvocationContext"/> — the filter only uses HttpContext.</summary>
    private sealed class StubInvocationContext : EndpointFilterInvocationContext
    {
        public StubInvocationContext(HttpContext httpContext) => HttpContext = httpContext;

        public override HttpContext HttpContext { get; }

        public override IList<object?> Arguments { get; } = [];

        public override T GetArgument<T>(int index) => throw new NotSupportedException();
    }
}
