using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Shared.Security;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Web;

/// <summary>
/// The idempotency filter keeps its own entries in L2 — it reads and writes them through
/// <see cref="IDistributedCache"/> itself, because HybridCache has no get-only probe
/// (dotnet/aspnetcore#57191) and the payload it frames into L2 is not readable by anything else
/// (issue #82: the old split — write through HybridCache, probe through IDistributedCache — made the
/// first replay of every key a 500 against Redis, and a silent no-op without it).
/// </summary>
/// <remarks>
/// <para>
/// Driven against the filter with a real <see cref="MemoryDistributedCache"/> underneath: used
/// directly it behaves exactly like any other <see cref="IDistributedCache"/>, which is the whole
/// point — HybridCache is the thing that ignores it as an L2, not the filter. A thin recording
/// wrapper over it is how these tests can name the physical key and read the stored bytes.
/// </para>
/// <para>
/// Every test here drives the filter the way the minimal-API pipeline does: whatever the filter
/// returns is executed as an <see cref="IResult"/> against the same <see cref="HttpContext"/>. That
/// is what makes "the second response is the first response" an assertion about bytes on the wire
/// rather than about an object graph.
/// </para>
/// </remarks>
public sealed class IdempotencyEndpointFilterTests
{
    private const string HeaderName = "Idempotency-Key";
    private const string ReplayedHeader = "Idempotency-Replayed";

    private static readonly JsonSerializerOptions EntryJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class FixedTenantAccessor : ICacheTenantAccessor
    {
        public string? TenantId { get; set; }
    }

    /// <summary>
    /// A real <see cref="MemoryDistributedCache"/> that also records which keys were read, which were
    /// written and with what expiration — so a test can assert the physical key and the TTL rather
    /// than infer them.
    /// </summary>
    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        private readonly List<string> _reads = [];
        private readonly Dictionary<string, DistributedCacheEntryOptions> _writes = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Reads
        {
            get { lock (_reads) { return [.. _reads]; } }
        }

        public IReadOnlyCollection<string> WrittenKeys
        {
            get { lock (_reads) { return [.. _writes.Keys]; } }
        }

        public DistributedCacheEntryOptions OptionsFor(string key)
        {
            lock (_reads) { return _writes[key]; }
        }

        /// <summary>Writes bytes the filter did not write — corrupt entries, foreign payloads.</summary>
        public void Poison(string key, byte[] value) => _inner.Set(key, value, new DistributedCacheEntryOptions());

        public byte[]? Get(string key)
        {
            lock (_reads) { _reads.Add(key); }
            return _inner.Get(key);
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            lock (_reads) { _reads.Add(key); }
            return _inner.GetAsync(key, token);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            lock (_reads) { _writes[key] = options; }
            _inner.Set(key, value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            lock (_reads) { _writes[key] = options; }
            return _inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

        public void Remove(string key) => _inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    }

    private static (ServiceProvider Provider, FixedTenantAccessor Tenant, RecordingDistributedCache L2) BuildServices(
        TimeSpan? ttl = null,
        TimeSpan? lockWait = null)
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
        if (ttl is not null)
        {
            services.Configure<IdempotencyOptions>(o => o.DefaultTtl = ttl.Value);
        }
        if (lockWait is not null)
        {
            services.Configure<IdempotencyOptions>(o => o.LockWaitTimeout = lockWait.Value);
        }
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return (services.BuildServiceProvider(), tenant, l2);
    }

    private sealed class Counter
    {
        public int Executions { get; set; }
    }

    private sealed record Payload(string Name);

    /// <summary>A payload whose second field reads as a secret by name alone.</summary>
    private sealed record Signup(string Name, string Password);

    /// <summary>A payload whose second field does not read as a secret and says so at the property.</summary>
    private sealed record Enrolment(string Name, [property: NotFingerprinted] string Voucher);

    private sealed record Created(int Id);

    /// <summary>A response body that has gone away, the way a client that hung up mid-write has.</summary>
    private sealed class HungUpStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("the client hung up");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("the client hung up");
    }

    private sealed record Invocation(int StatusCode, string Body, IHeaderDictionary Headers)
    {
        public bool Replayed => Headers.ContainsKey(ReplayedHeader);
    }

    /// <summary>
    /// Runs the filter and then executes whatever it returned, exactly as the minimal-API pipeline
    /// would, so the assertions below are about the response the caller receives.
    /// </summary>
    private static async Task<Invocation> InvokeAsync(
        IServiceProvider provider,
        string idempotencyKey,
        Counter counter,
        ClaimsPrincipal? user = null,
        object? payload = null,
        string path = "/things",
        EndpointFilterDelegate? handler = null,
        Stream? responseBody = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = path;
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            httpContext.Request.Headers[HeaderName] = idempotencyKey;
        }

        var body = new MemoryStream();
        httpContext.Response.Body = responseBody ?? body;
        if (user is not null)
        {
            httpContext.User = user;
        }

        handler ??= ctx =>
        {
            counter.Executions++;
            return ValueTask.FromResult<object?>(TypedResults.Created($"/things/{counter.Executions}", new Created(counter.Executions)));
        };

        var filter = new IdempotencyEndpointFilter();
        IList<object?> arguments = payload is null ? [] : [payload];
        var result = await filter.InvokeAsync(new StubInvocationContext(httpContext, arguments), handler);

        if (result is IResult executable)
        {
            await executable.ExecuteAsync(httpContext);
        }

        return new Invocation(httpContext.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()), httpContext.Response.Headers);
    }

    private static string IdempotencyKeyIn(RecordingDistributedCache l2) => l2.WrittenKeys.Single();

    [Fact]
    public async Task Second_Call_Should_Replay_Status_Body_And_AllowedHeaders_Without_Running_TheHandler()
    {
        var (provider, tenant, _) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            var first = await InvokeAsync(provider, "req-1", counter, payload: new Payload("a"));
            var second = await InvokeAsync(provider, "req-1", counter, payload: new Payload("a"));

            counter.Executions.ShouldBe(1, "the second request must not reach the handler.");
            second.StatusCode.ShouldBe(first.StatusCode);
            second.StatusCode.ShouldBe(StatusCodes.Status201Created,
                "the captured status is the one the result produced, not the 200 the response carries before it executes.");
            second.Body.ShouldBe(first.Body);
            second.Body.ShouldBe("""{"id":1}""", "the replay is the response body, not a serialized IResult wrapper.");
            second.Headers.Location.ToString().ShouldBe("/things/1");
            second.Replayed.ShouldBeTrue();
            first.Replayed.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Entry_Should_Land_Under_The_TenantScoped_PhysicalKey()
    {
        // The filter reads and writes L2 by key itself, so the key has to carry the tenant the same
        // way CacheKeyScope would — an unprefixed key is the shared bucket #77 removed.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            await InvokeAsync(provider, "req-1", new Counter());

            var key = IdempotencyKeyIn(l2);
            key.ShouldStartWith("t:alpha:idem:");
            key.ShouldEndWith(":req-1");
            l2.Reads.ShouldContain(key, "the probe must look under exactly the key the write path stored.");
        }
    }

    [Fact]
    public async Task No_Tenant_Should_Use_The_Global_Namespace_Not_A_Tenant_Named_Global()
    {
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = null;
            await InvokeAsync(provider, "req-3", new Counter());

            var key = IdempotencyKeyIn(l2);
            key.ShouldStartWith("g:idem:");
            key.ShouldNotStartWith("t:global:");
        }
    }

    [Fact]
    public async Task SameKey_In_TwoTenants_Should_Not_Share_A_Replay()
    {
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            var counter = new Counter();

            tenant.TenantId = "alpha";
            var first = await InvokeAsync(provider, "shared-key", counter);

            tenant.TenantId = "beta";
            var second = await InvokeAsync(provider, "shared-key", counter);

            counter.Executions.ShouldBe(2, "tenant B must never replay tenant A's response.");
            second.Replayed.ShouldBeFalse();
            second.Body.ShouldNotBe(first.Body);
            l2.WrittenKeys.Count.ShouldBe(2);
            l2.WrittenKeys.ShouldAllBe(k => k.EndsWith(":shared-key", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SameKey_From_TwoUsers_Of_OneTenant_Should_Not_Share_A_Replay()
    {
        // The client key is chosen by the client. Two users of one tenant picking the same one — or
        // one guessing another's — must not be able to read each other's response.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "shared-key", counter, user: UserWithId("user-1"));
            var second = await InvokeAsync(provider, "shared-key", counter, user: UserWithId("user-2"));

            counter.Executions.ShouldBe(2);
            second.Replayed.ShouldBeFalse();
            l2.WrittenKeys.Count.ShouldBe(2);
        }
    }

    [Fact]
    public async Task SameKey_On_A_DifferentRoute_Should_Not_Share_A_Replay()
    {
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "shared-key", counter, path: "/things");
            var second = await InvokeAsync(provider, "shared-key", counter, path: "/other-things");

            counter.Executions.ShouldBe(2);
            second.Replayed.ShouldBeFalse();
            l2.WrittenKeys.Count.ShouldBe(2);
        }
    }

    [Fact]
    public async Task SameKey_With_A_DifferentPayload_Should_Be_Refused_As_A_KeyReuse()
    {
        // Replaying here would answer a request nobody made. 422 is the IETF Idempotency-Key draft's
        // status for a key reused with a different payload.
        var (provider, tenant, _) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "req-1", counter, payload: new Payload("first"));
            var second = await InvokeAsync(provider, "req-1", counter, payload: new Payload("second"));

            counter.Executions.ShouldBe(1, "a mismatched retry must not run the handler either.");
            second.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
            second.Replayed.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Malformed_Entry_Should_Be_A_Miss_And_Get_Overwritten()
    {
        // The exact shape of the bug: HybridCache's framed L2 payload (03 01 …) under our key. It has
        // to run the handler, not throw.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "req-1", counter);
            var key = IdempotencyKeyIn(l2);
            l2.Poison(key, [0x03, 0x01, 0x7B, 0x00, 0xFF]);

            var second = await InvokeAsync(provider, "req-1", counter);

            counter.Executions.ShouldBe(2, "an unreadable entry is a miss, never a 500.");
            second.StatusCode.ShouldBe(StatusCodes.Status201Created);
            second.Replayed.ShouldBeFalse();

            // And the entry it left behind is readable again.
            var third = await InvokeAsync(provider, "req-1", counter);
            third.Replayed.ShouldBeTrue();
            counter.Executions.ShouldBe(2);
        }
    }

    [Fact]
    public async Task Entry_From_An_Unknown_Version_Should_Be_A_Miss()
    {
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "req-1", counter);
            var key = IdempotencyKeyIn(l2);
            l2.Poison(key, Encoding.UTF8.GetBytes("""{"v":99,"statusCode":201,"body":""}"""));

            var second = await InvokeAsync(provider, "req-1", counter);

            counter.Executions.ShouldBe(2, "a format this build does not know must be a miss, not a guess.");
            second.Replayed.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Empty_Entry_Should_Be_A_Miss()
    {
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "req-1", counter);
            l2.Poison(IdempotencyKeyIn(l2), []);

            (await InvokeAsync(provider, "req-1", counter)).Replayed.ShouldBeFalse();
            counter.Executions.ShouldBe(2);
        }
    }

    [Fact]
    public async Task NonSuccess_Responses_Should_Not_Be_Stored()
    {
        // A 409 is the answer to this attempt, not a durable outcome: caching it would freeze a
        // transient failure in place for the whole TTL.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            EndpointFilterDelegate failing = ctx =>
            {
                counter.Executions++;
                return ValueTask.FromResult<object?>(TypedResults.Conflict("nope"));
            };

            var first = await InvokeAsync(provider, "req-1", counter, handler: failing);
            var second = await InvokeAsync(provider, "req-1", counter, handler: failing);

            first.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
            counter.Executions.ShouldBe(2);
            second.Replayed.ShouldBeFalse();
            l2.WrittenKeys.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task SetCookie_Should_Never_Be_Stored_Or_Replayed()
    {
        // The reason the stored header set is an allow-list: a replay hands one caller a response
        // another caller produced, and a session cookie in it would be that caller's session.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            EndpointFilterDelegate cookieSetting = ctx =>
            {
                counter.Executions++;
                ctx.HttpContext.Response.Headers.SetCookie = "refresh=super-secret; HttpOnly";
                return ValueTask.FromResult<object?>(TypedResults.Ok(new Created(1)));
            };

            await InvokeAsync(provider, "req-1", counter, handler: cookieSetting);

            var stored = Encoding.UTF8.GetString(
                new MemoryDistributedCacheProbe(l2).Read(IdempotencyKeyIn(l2)));
            stored.ShouldNotContain("super-secret");
            stored.Contains("set-cookie", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();

            var second = await InvokeAsync(provider, "req-1", counter, handler: cookieSetting);

            second.Replayed.ShouldBeTrue();
            second.Headers.ContainsKey("Set-Cookie").ShouldBeFalse(
                "a replay must never hand one caller another caller's cookie.");
        }
    }

    [Fact]
    public async Task Ttl_Should_Come_From_Options()
    {
        var (provider, tenant, l2) = BuildServices(ttl: TimeSpan.FromMinutes(7));
        await using (provider)
        {
            tenant.TenantId = "alpha";
            await InvokeAsync(provider, "req-1", new Counter());

            l2.OptionsFor(IdempotencyKeyIn(l2)).AbsoluteExpirationRelativeToNow.ShouldBe(TimeSpan.FromMinutes(7));
        }
    }

    [Fact]
    public async Task Concurrent_Duplicates_Should_Run_TheHandler_Once()
    {
        // IDistributedCache has no set-if-absent, so this is an in-process lock and covers one
        // instance — see KeyedAsyncLock for the multi-instance window it does not close.
        var (provider, tenant, _) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            var runs = 0;

            EndpointFilterDelegate slow = async ctx =>
            {
                Interlocked.Increment(ref runs);
                entered.TrySetResult();
                await release.Task;
                return TypedResults.Created("/things/1", new Created(1));
            };

            var first = InvokeAsync(provider, "req-1", counter, handler: slow);
            await entered.Task;
            var second = InvokeAsync(provider, "req-1", counter, handler: slow);
            release.SetResult();

            var results = await Task.WhenAll(first, second);

            runs.ShouldBe(1, "two simultaneous duplicates must not both execute.");
            results[1].Replayed.ShouldBeTrue();
            results[1].Body.ShouldBe(results[0].Body);
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

            await InvokeAsync(provider, string.Empty, counter);

            counter.Executions.ShouldBe(1);
            l2.Reads.ShouldBeEmpty();
            l2.WrittenKeys.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task OverLong_Key_Should_Be_Rejected_Before_TheCache_Is_Touched()
    {
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            var result = await InvokeAsync(provider, new string('k', 129), counter);

            result.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
            counter.Executions.ShouldBe(0);
            l2.Reads.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Stored_Entry_Should_Carry_TheCurrent_Wire_Version()
    {
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            await InvokeAsync(provider, "req-1", new Counter());

            var entry = JsonSerializer.Deserialize<CachedIdempotentResponse>(
                new MemoryDistributedCacheProbe(l2).Read(IdempotencyKeyIn(l2)), EntryJson);

            entry.ShouldNotBeNull();
            entry.V.ShouldBe(CachedIdempotentResponse.CurrentVersion);
            entry.StatusCode.ShouldBe(StatusCodes.Status201Created);
        }
    }

    [Fact]
    public async Task A_Field_That_Reads_As_A_Secret_Should_Not_Enter_TheFingerprint()
    {
        // The fingerprint lives in Redis for the entry's whole TTL, and an unsalted digest of a
        // password is a password for anyone willing to grind a candidate list. The cost is stated
        // where it is paid: a retry differing *only* in the password replays instead of answering 422.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "req-1", counter, payload: new Signup("ada", "Sup3rStr0ng!"));
            var retry = await InvokeAsync(provider, "req-1", counter, payload: new Signup("ada", "something-else"));

            retry.Replayed.ShouldBeTrue();
            counter.Executions.ShouldBe(1);

            var stored = Encoding.UTF8.GetString(new MemoryDistributedCacheProbe(l2).Read(IdempotencyKeyIn(l2)));
            stored.ShouldNotContain("Sup3rStr0ng!");
        }
    }

    [Fact]
    public async Task A_NonSecret_Field_Should_Still_Be_Fingerprinted()
    {
        // The control for the test above: dropping the password must not amount to dropping the
        // fingerprint, or key reuse with a genuinely different request would quietly replay.
        var (provider, tenant, _) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "req-1", counter, payload: new Signup("ada", "Sup3rStr0ng!"));
            var other = await InvokeAsync(provider, "req-1", counter, payload: new Signup("grace", "Sup3rStr0ng!"));

            other.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        }
    }

    [Fact]
    public async Task A_NotFingerprinted_Field_Should_Not_Enter_TheFingerprint()
    {
        // The explicit half, for a field whose name does not give it away.
        var (provider, tenant, _) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await InvokeAsync(provider, "req-1", counter, payload: new Enrolment("ada", "first"));
            var retry = await InvokeAsync(provider, "req-1", counter, payload: new Enrolment("ada", "second"));

            retry.Replayed.ShouldBeTrue();
            counter.Executions.ShouldBe(1);
        }
    }

    [Fact]
    public async Task OverLong_Key_Should_Answer_ProblemDetails()
    {
        // Every other client error the API answers is a ProblemDetails; a bare string body here would
        // be the one 400 a client has to special-case.
        var (provider, tenant, _) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";

            var result = await InvokeAsync(provider, new string('k', 129), new Counter());

            result.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
            result.Body.ShouldContain("\"title\":\"Invalid idempotency key\"");
            result.Body.ShouldContain("128");
        }
    }

    [Fact]
    public async Task Entry_Should_Be_Stored_Before_TheBody_Reaches_TheClient()
    {
        // The handler has committed its side effect by the time the response is written. Storing
        // after the write would leave no entry when the client hangs up mid-write, and the retry
        // would run that side effect a second time — the one thing the key is there to prevent.
        var (provider, tenant, l2) = BuildServices();
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();

            await Should.ThrowAsync<IOException>(
                () => InvokeAsync(provider, "req-1", counter, responseBody: new HungUpStream()));

            counter.Executions.ShouldBe(1);
            l2.WrittenKeys.ShouldHaveSingleItem("the entry must already be in the cache when the write fails.");

            var retry = await InvokeAsync(provider, "req-1", counter);
            retry.Replayed.ShouldBeTrue();
            counter.Executions.ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_Duplicate_Should_Stop_Waiting_After_TheLock_Timeout_And_Run_Anyway()
    {
        // The lock is held across the whole handler, so an unbounded wait would park every duplicate
        // of one slow request for as long as it takes, a request thread apiece. Timing out widens the
        // same "the handler may run twice" window that is already open across instances.
        var (provider, tenant, _) = BuildServices(lockWait: TimeSpan.FromMilliseconds(50));
        await using (provider)
        {
            tenant.TenantId = "alpha";
            var counter = new Counter();
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            var runs = 0;

            EndpointFilterDelegate slow = async ctx =>
            {
                Interlocked.Increment(ref runs);
                entered.TrySetResult();
                await release.Task;
                return TypedResults.Created("/things/1", new Created(1));
            };

            EndpointFilterDelegate prompt = ctx =>
            {
                Interlocked.Increment(ref runs);
                return ValueTask.FromResult<object?>(TypedResults.Created("/things/2", new Created(2)));
            };

            var first = InvokeAsync(provider, "req-1", counter, handler: slow);
            await entered.Task;
            var second = await InvokeAsync(provider, "req-1", counter, handler: prompt);

            second.Replayed.ShouldBeFalse("the waiter gave up on the lock and ran the handler itself.");
            runs.ShouldBe(2);

            release.SetResult();
            await first;
        }
    }

    private static ClaimsPrincipal UserWithId(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    /// <summary>Reads raw entry bytes back out of the cache under test.</summary>
    private sealed class MemoryDistributedCacheProbe(IDistributedCache cache)
    {
        public byte[] Read(string key) => cache.Get(key) ?? [];
    }

    /// <summary>Minimal <see cref="EndpointFilterInvocationContext"/> — the filter uses HttpContext and Arguments.</summary>
    private sealed class StubInvocationContext(HttpContext httpContext, IList<object?> arguments) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;

        public override IList<object?> Arguments { get; } = arguments;

        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }
}
