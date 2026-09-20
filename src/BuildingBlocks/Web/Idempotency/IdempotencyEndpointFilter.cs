using System.IO.Pipelines;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Boilerplate.BuildingBlocks.Web.Idempotency;

/// <summary>
/// Endpoint filter that provides idempotency for POST/PUT/PATCH requests. When an Idempotency-Key
/// header is present, the response is captured and replayed for a later request that carries the
/// same key from the same caller to the same endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>The filter owns its entry format and reads <i>and</i> writes it through
/// <see cref="IDistributedCache"/>.</b> It used to write through <c>HybridCache</c> and probe with
/// <c>IDistributedCache</c>, which cannot work: HybridCache frames its L2 payload (version byte,
/// expiry, key, tags, then the value), so the probe read bytes that are not JSON and threw — the
/// first replay of every key answered 500 against a real Redis, and answered nothing at all with the
/// in-memory fallback, which HybridCache ignores as an L2 (issue #82). A replay store needs exactly
/// get and set-with-TTL, which is what <c>IDistributedCache</c> is; HybridCache's get-only probe gap
/// is dotnet/aspnetcore#57191. An architecture test keeps this file on the short allow-list of
/// application code permitted to touch <c>IDistributedCache</c> at all.
/// </para>
/// <para>
/// <b>Tenant scoping still comes from the Caching block.</b> The physical key is
/// <see cref="CacheKeyScope"/>'s — <c>TenantKey</c> when a tenant is ambient, <c>GlobalKey</c> when
/// there is none — so idempotency entries live in the same namespaces as everything else and one
/// tenant cannot read another's.
/// </para>
/// <para>
/// <b>An entry that cannot be read is a miss, never a 500.</b> Foreign bytes, malformed JSON, an
/// unknown <see cref="CachedIdempotentResponse.V"/> — each logs once at warning and runs the handler,
/// overwriting the entry.
/// </para>
/// <para>
/// <b>What the key is bound to.</b> Tenant (through the namespace) + subject (or an anonymous
/// marker) + method + path, hashed into the key, so a key can only ever replay the caller's own
/// response to the same endpoint. The request payload is fingerprinted separately and stored in the
/// entry: the same key with a <i>different</i> payload is a client bug and answers
/// <c>422 Unprocessable Entity</c> rather than replaying a response to a request nobody made.
/// </para>
/// <para>
/// <b>Secrets never enter the fingerprint.</b> The bound arguments are hashed <i>after</i> every
/// property that reads as a secret is dropped — by name (<see cref="SensitiveFieldNames"/>) and by
/// <see cref="NotFingerprintedAttribute"/>. An unsalted digest of a password in Redis is a password
/// in Redis for anyone willing to grind a candidate list, and keying the hash would not help: the
/// Data Protection keys are persisted in the same Redis. The cost is stated where it is paid — a
/// retry that differs <i>only</i> in an excluded field replays instead of answering 422.
/// </para>
/// <para>
/// <b>An anonymous route is never marked idempotent.</b> For an anonymous caller there is no subject
/// to bind the partition to, so it collapses to the client-supplied key alone: anyone who presents
/// another caller's key on that route is handed their stored response. <c>SelfRegisterUser</c> used
/// to be the one exception (#84) — it is not anymore, and it did not need to be: a sequential retry
/// of a registration is already safe, because <c>UserRegistrationService</c> refuses a duplicate
/// email/username with 400 rather than creating a second user. This also covers a token-issuing
/// endpoint — the same anonymous-partition hazard is why none of the anonymous
/// <c>tenants/{tenant}/auth/*</c> routes that set a refresh cookie are marked idempotent either;
/// response headers are stored by allow-list anyway, so <c>Set-Cookie</c> never reaches the cache
/// regardless.
/// <c>EndpointAuthorizationIntentTests</c>-adjacent
/// <c>IdempotentEndpointAnonymityTests</c> (Integration.Tests) is what pins the rule: it reads the
/// running host's built <see cref="Microsoft.AspNetCore.Http.Endpoint.Metadata"/>, so it sees
/// anonymity declared as a route group, an <c>[AllowAnonymous]</c> attribute, or a chain call alike —
/// every form this repository uses. <c>AnonymousRoutesAreNeverIdempotentTests</c> in
/// Architecture.Tests is a cheap, text-only early warning for the chain-call and
/// <c>[AllowAnonymous]</c> attribute forms on a route's own chain; it cannot see route-group
/// anonymity, which is why the integration test is the one that is authoritative.
/// </para>
/// <para>
/// <b>A response that carries a short-lived capability is never marked idempotent either.</b>
/// <c>RequestUploadUrl</c> used to be (#85): its response is a presigned PUT URL valid for minutes,
/// but a replay entry lives for the idempotency TTL (24h), so a retry under the same key past the
/// URL's expiry was handed a dead link rather than a fresh one. A repeated call is harmless without
/// replay — it creates another pending <c>FileAsset</c> whose <c>UploadDeadline</c> passes and which
/// <c>PurgeOrphanedFilesJob</c> deletes. The general rule an endpoint like this has to weigh: a
/// response that expires sooner than the replay entry's TTL must not be marked idempotent.
/// </para>
/// <para>
/// <b>Nothing may wrap it.</b> The filter writes the response itself and returns
/// <see cref="TypedResults.Empty"/>, so any endpoint filter added <i>before</i> it on the same route
/// wraps it, sees <c>Empty</c> where it expected the handler's result, and runs its post-<c>next</c>
/// code after the bytes have already gone out. <c>.WithIdempotency()</c> must therefore be the first
/// endpoint filter in the chain (filters run outermost-first in the order they are added);
/// <c>IdempotencyFilterOrderTests</c> in Architecture.Tests fails the build if another one precedes
/// it. A filter added <i>after</i> it is fine — it sits between this filter and the handler, and its
/// result and headers are captured normally.
/// </para>
/// <para>
/// <b>There is deliberately no <c>MaxCachedBodyBytes</c>.</b> Every idempotent endpoint in the kit
/// answers with a small created-resource DTO, and Kestrel already caps the <i>request</i> at 10 MiB,
/// so a cap here would be a knob with no setting to find. What would force one: marking an endpoint
/// idempotent whose success body is unbounded — a list, an export, anything streamed. At that point
/// add the cap and skip the store (not the response) when the buffer exceeds it, because this filter
/// buffers the whole body in memory before it writes, and a large body would be held twice, once in
/// the buffer and once in the entry.
/// </para>
/// </remarks>
public sealed class IdempotencyEndpointFilter : IEndpointFilter
{
    private const string ReplayedHeader = "Idempotency-Replayed";
    private const string AnonymousSubject = "anon";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The serializer the <i>fingerprint</i> uses: the entry format's shape minus every property that
    /// reads as a secret. The exclusion lives in the contract resolver rather than at the call site
    /// so it reaches nested objects too — a password one level down is still a password in Redis.
    /// </summary>
    private static readonly JsonSerializerOptions FingerprintJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropSensitiveProperties } },
    };

    /// <summary>
    /// The only response headers that are stored and replayed. An allow-list, not a deny-list: the
    /// one header that must never be replayed to a second caller is <c>Set-Cookie</c>, and a
    /// deny-list is a place to forget the next one like it.
    /// </summary>
    private static readonly string[] ReplayableResponseHeaders = ["Location", "ETag", "Content-Language"];

    /// <summary>
    /// Serialises duplicates within this process — see <see cref="KeyedAsyncLock"/> for the
    /// multi-instance window it deliberately does not close.
    /// </summary>
    private static readonly KeyedAsyncLock InFlight = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var options = httpContext.RequestServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value;
        var idempotencyKey = httpContext.Request.Headers[options.HeaderName].ToString();

        // No header = pass through (idempotency is opt-in per request).
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return await next(context).ConfigureAwait(false);
        }

        if (idempotencyKey.Length > options.MaxKeyLength)
        {
            // ProblemDetails, like every other client error the API answers: a bare string body here
            // would be the one 400 a client has to special-case.
            return TypedResults.Problem(
                detail: $"Idempotency key exceeds the maximum length of {options.MaxKeyLength} characters.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid idempotency key");
        }

        var cache = httpContext.RequestServices.GetRequiredService<IDistributedCache>();
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<IdempotencyEndpointFilter>>();
        var cacheKey = CacheKey(httpContext, idempotencyKey);
        var fingerprint = RequestFingerprint(context, logger);

        var replay = await TryReplayAsync(cache, cacheKey, fingerprint, httpContext, logger, idempotencyKey, logUnreadable: true)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        // Two duplicates arriving together must not both run the handler. The probe above is the
        // cheap path; this is the one that is allowed to wait, and it re-probes because the request
        // it waited for has just written the entry. The wait is bounded: the lock is held across the
        // whole handler, so an unbounded one would queue every duplicate of a slow request behind it.
        using var handle = await InFlight.TryAcquireAsync(cacheKey, options.LockWaitTimeout, httpContext.RequestAborted)
            .ConfigureAwait(false);

        if (handle is null)
        {
            logger.LogWarning(
                "Timed out after {Timeout} waiting for an in-flight request with idempotency key {KeyHash}; running the handler without the lock.",
                options.LockWaitTimeout,
                HashKey(idempotencyKey));
        }
        else
        {
            replay = await TryReplayAsync(cache, cacheKey, fingerprint, httpContext, logger, idempotencyKey, logUnreadable: false)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }
        }

        var result = await next(context).ConfigureAwait(false);
        var captured = await CaptureAsync(result, httpContext).ConfigureAwait(false);

        // Only a success is replayable. A 4xx/5xx is the server's answer to *this* attempt — caching
        // it would make a transient failure permanent for the lifetime of the key.
        //
        // Stored *before* the body reaches the client, and not on the request's token. The handler has
        // already committed its side effect by now; if the client hangs up while the response is being
        // written, storing afterwards would leave no entry, and the retry would run the side effect a
        // second time — the one thing the key is there to prevent.
        if (captured.StatusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices)
        {
            await StoreAsync(cache, cacheKey, captured, fingerprint, options, logger, idempotencyKey)
                .ConfigureAwait(false);
        }

        await FlushAsync(httpContext, captured.Body).ConfigureAwait(false);

        // The response is already written; returning the captured result again would duplicate it.
        return TypedResults.Empty;
    }

    /// <summary>
    /// The physical cache key: the caller/endpoint binding hashed into a logical name, then scoped
    /// by the Caching block — tenant namespace when a tenant is ambient, global namespace when not.
    /// </summary>
    private static string CacheKey(HttpContext httpContext, string idempotencyKey)
    {
        var keyScope = httpContext.RequestServices.GetRequiredService<CacheKeyScope>();

        // Subject, method and path are hashed together rather than concatenated into the key: a
        // subject id or path containing the separator could otherwise be arranged to name another
        // caller's partition. The client key stays readable at the tail, where nothing follows it.
        var binding = Hash($"{SubjectId(httpContext) ?? AnonymousSubject}\n{httpContext.Request.Method}\n{httpContext.Request.Path}");
        var logicalKey = CacheKeys.IdempotencyEntry(binding, idempotencyKey);

        // No tenant at all: declare the entry global rather than invent a tenant for it. Nothing in
        // the kit reaches this branch today (both idempotent endpoints resolve a tenant), so it is
        // the defensive path for a future tenant-less idempotent endpoint.
        return keyScope.HasTenant ? keyScope.TenantKey(logicalKey) : CacheKeyScope.GlobalKey(logicalKey);
    }

    /// <summary>
    /// Reads the entry and turns it into the response to return, or <see langword="null"/> to run the
    /// handler. Every failure mode below is a miss: an unreadable entry must never surface as a 500.
    /// </summary>
    private static async Task<object?> TryReplayAsync(
        IDistributedCache cache,
        string cacheKey,
        string? fingerprint,
        HttpContext httpContext,
        ILogger logger,
        string idempotencyKey,
        bool logUnreadable)
    {
        byte[]? payload;
        try
        {
            payload = await cache.GetAsync(cacheKey, httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cache outage must not fail the request: idempotency is a convenience, the handler is the truth.
            logger.LogWarning(ex, "Idempotency probe failed for key {KeyHash}; running the handler.", HashKey(idempotencyKey));
            return null;
        }

        if (payload is null || payload.Length == 0)
        {
            return null;
        }

        var entry = Deserialize(payload);
        if (entry is null || entry.V != CachedIdempotentResponse.CurrentVersion)
        {
            if (logUnreadable)
            {
                logger.LogWarning(
                    "Unreadable idempotency entry for key {KeyHash} (version {Version}); treating it as a miss and overwriting it.",
                    HashKey(idempotencyKey),
                    entry?.V);
            }

            return null;
        }

        if (fingerprint is not null && entry.RequestFingerprint is not null
            && !string.Equals(entry.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            logger.LogWarning("Idempotency key {KeyHash} was reused with a different request payload.", HashKey(idempotencyKey));
            return TypedResults.Problem(
                detail: "This Idempotency-Key was already used for a request with a different payload. Use a new key.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency key reuse");
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Idempotent replay for key {KeyHash}", HashKey(idempotencyKey));
        }

        return new ReplayResult(entry);
    }

    private static CachedIdempotentResponse? Deserialize(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<CachedIdempotentResponse>(payload, JsonOpts);
        }
        catch (JsonException)
        {
            // Foreign bytes under our key — a HybridCache-framed payload from an older build, a
            // half-written value, anything. Miss.
            return null;
        }
    }

    /// <summary>
    /// Runs the endpoint's result against a buffered body so the bytes the client receives are the
    /// bytes that get cached. The caller stores the entry and only then flushes, so a client that
    /// disconnects mid-write still leaves a replayable entry behind.
    /// </summary>
    /// <remarks>
    /// Executing the result here rather than handing it back is what makes the replay honest. The
    /// filter runs <i>before</i> the result executes, so without this the status code is still the
    /// default 200 and the "body" would be the serialized <c>IResult</c> wrapper (<c>Created&lt;T&gt;</c>
    /// with its Location and Value properties), not the response the caller actually got. It is also
    /// the only way to know whether the response was a success before deciding to store it.
    /// </remarks>
    private static async Task<CapturedResponse> CaptureAsync(object? result, HttpContext httpContext)
    {
        var response = httpContext.Response;
        var originalBody = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await ExecuteResultAsync(result, httpContext).ConfigureAwait(false);
        }
        finally
        {
            response.Body = originalBody;
        }

        var body = buffer.ToArray();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ReplayableResponseHeaders)
        {
            if (response.Headers.TryGetValue(name, out var value) && !StringValues.IsNullOrEmpty(value))
            {
                headers[name] = value.ToString();
            }
        }

        return new CapturedResponse(response.StatusCode, response.ContentType, body, headers);
    }

    /// <summary>
    /// Writes the captured bytes to the real response body. Runs last, after the entry is stored:
    /// this is the call that can fail on a client disconnect, and by then there is nothing left to
    /// lose.
    /// </summary>
    private static async Task FlushAsync(HttpContext httpContext, byte[] body)
    {
        if (body.Length > 0)
        {
            await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The same three cases the minimal-API pipeline handles when a filter chain returns a value:
    /// an <see cref="IResult"/>, a string, anything else as JSON.
    /// </summary>
    private static Task ExecuteResultAsync(object? result, HttpContext httpContext) => result switch
    {
        null => Task.CompletedTask,
        IResult typed => typed.ExecuteAsync(httpContext),
        string text => WriteTextAsync(httpContext, text),
        _ => httpContext.Response.WriteAsJsonAsync(result, result.GetType(), options: null, contentType: null, httpContext.RequestAborted),
    };

    private static Task WriteTextAsync(HttpContext httpContext, string text)
    {
        httpContext.Response.ContentType = "text/plain; charset=utf-8";
        return httpContext.Response.WriteAsync(text, httpContext.RequestAborted);
    }

    private static async Task StoreAsync(
        IDistributedCache cache,
        string cacheKey,
        CapturedResponse captured,
        string? fingerprint,
        IdempotencyOptions options,
        ILogger logger,
        string idempotencyKey)
    {
        try
        {
            var entry = new CachedIdempotentResponse
            {
                StatusCode = captured.StatusCode,
                ContentType = captured.ContentType,
                RequestFingerprint = fingerprint,
                Headers = captured.Headers.Count > 0 ? captured.Headers : null,
                Body = captured.Body,
            };

            var payload = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOpts);
            var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.DefaultTtl };

            // CancellationToken.None on purpose: the request's token is already cancelled when the
            // client hangs up, and that is exactly the case where the entry matters most. The write
            // is a small SET against the cache, not something worth keeping a cancelled request alive.
            await cache.SetAsync(cacheKey, payload, entryOptions, CancellationToken.None).ConfigureAwait(false);
        }
        // Best-effort caching: idempotency replay is a convenience, not a correctness requirement.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to cache idempotent response for key {KeyHash}", HashKey(idempotencyKey));
        }
    }

    /// <summary>
    /// A hash of the request payload: query string plus every bound argument that is data rather
    /// than plumbing, minus everything that reads as a secret. <see langword="null"/> when the payload
    /// cannot be serialized, in which case the entry carries no fingerprint and reuse of the key is
    /// replayed rather than refused — a miss on a client bug, never a false 422 on a legitimate retry.
    /// </summary>
    /// <remarks>
    /// The raw body is not available here: an endpoint filter runs after model binding, by which
    /// point the body stream has been consumed and is not rewindable unless something called
    /// <c>EnableBuffering</c> first. The bound arguments are the same data, already materialised —
    /// which is also what makes dropping the secret ones possible at all, since a raw body could only
    /// be hashed whole.
    /// </remarks>
    private static string? RequestFingerprint(EndpointFilterInvocationContext context, ILogger logger)
    {
        var httpContext = context.HttpContext;

        // Parameter names, for the endpoint that binds a bare value rather than a DTO: Arguments is
        // positional against the handler's signature, so index i is parameter i. Metadata is absent
        // outside a mapped endpoint, and then only the property-level exclusion applies.
        var parameters = httpContext.GetEndpoint()?.Metadata.GetMetadata<MethodInfo>()?.GetParameters();

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(httpContext.Request.QueryString.Value ?? string.Empty));

            for (var i = 0; i < context.Arguments.Count; i++)
            {
                var argument = context.Arguments[i];
                if (!IsRequestPayload(argument, httpContext.RequestServices) || IsSensitiveParameter(parameters, i))
                {
                    continue;
                }

                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(argument, argument!.GetType(), FingerprintJson));
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        // A parameter type System.Text.Json refuses to serialize. No fingerprint, no conflict check.
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            logger.LogDebug(ex, "Could not fingerprint the request; this key's entry will be replayed rather than checked.");
            return null;
        }
    }

    /// <summary>
    /// Drops every property that reads as a secret from the fingerprint's view of a type — by name
    /// (<see cref="SensitiveFieldNames"/>) and by <see cref="NotFingerprintedAttribute"/>. The name
    /// seen here is the serialized one, so it is the camelCase name the list is written against.
    /// </summary>
    private static void DropSensitiveProperties(JsonTypeInfo typeInfo)
    {
        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            var property = typeInfo.Properties[i];
            if (SensitiveFieldNames.IsSensitive(property.Name)
                || property.AttributeProvider?.IsDefined(typeof(NotFingerprintedAttribute), inherit: true) == true)
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// True for a handler parameter whose own name reads as a secret, or that is marked
    /// <see cref="NotFingerprintedAttribute"/> — the case <see cref="DropSensitiveProperties"/>
    /// cannot see, because a bare <c>string code</c> has no property to drop.
    /// </summary>
    private static bool IsSensitiveParameter(ParameterInfo[]? parameters, int index) =>
        parameters is not null
        && index < parameters.Length
        && (SensitiveFieldNames.IsSensitive(parameters[index].Name)
            || parameters[index].IsDefined(typeof(NotFingerprintedAttribute), inherit: true));

    /// <summary>
    /// True for an argument that is part of what the caller asked for, false for the plumbing the
    /// framework injects. Services are identified the way the framework identifies them —
    /// <see cref="IServiceProviderIsService"/> — so a handler's <c>IMediator</c> is not mistaken for
    /// payload just because its concrete type is not registered.
    /// </summary>
    private static bool IsRequestPayload(object? argument, IServiceProvider services)
    {
        if (argument is null
            or HttpContext or HttpRequest or HttpResponse
            or ClaimsPrincipal or CancellationToken
            or Stream or PipeReader
            or IFormFile or IFormFileCollection or IFormCollection)
        {
            return false;
        }

        if (services.GetService<IServiceProviderIsService>() is not { } isService)
        {
            return true;
        }

        var type = argument.GetType();
        return !isService.IsService(type) && !type.GetInterfaces().Any(isService.IsService);
    }

    /// <summary>
    /// The authenticated subject, or <see langword="null"/> for an anonymous caller. Part of the key
    /// binding: two users of one tenant that happen to pick the same Idempotency-Key get their own
    /// entries, and neither can read the other's response.
    /// </summary>
    private static string? SubjectId(HttpContext httpContext) =>
        httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? httpContext.User.FindFirst("sub")?.Value;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>A short, non-reversible stand-in for the client key, safe to log.</summary>
    private static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 8));

    private sealed record CapturedResponse(
        int StatusCode,
        string? ContentType,
        byte[] Body,
        Dictionary<string, string> Headers);

    /// <summary>
    /// Writes a stored response back out. Re-filters the headers on the way out as well as on the
    /// way in, so an entry written by an older build (or by anything else) still cannot replay a
    /// <c>Set-Cookie</c>.
    /// </summary>
    private sealed class ReplayResult(CachedIdempotentResponse entry) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var response = httpContext.Response;
            response.StatusCode = entry.StatusCode;
            if (entry.ContentType is not null)
            {
                response.ContentType = entry.ContentType;
            }

            if (entry.Headers is not null)
            {
                foreach (var (name, value) in entry.Headers)
                {
                    if (ReplayableResponseHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        response.Headers[name] = value;
                    }
                }
            }

            response.Headers[ReplayedHeader] = "true";

            if (entry.Body.Length > 0)
            {
                await response.Body.WriteAsync(entry.Body, httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Marker interface for <see cref="IdempotentEndpointMetadata"/>.</summary>
public interface IIdempotentEndpointMetadata;

/// <summary>
/// Marks an endpoint as carrying <see cref="IdempotencyEndpointFilter"/>, attached by
/// <see cref="IdempotencyEndpointExtensions.WithIdempotency"/> alongside the filter itself. Endpoint
/// metadata, not text, is what <c>EndpointAuthorizationIntentTests</c>-style guards can trust: a route
/// group's <c>.AllowAnonymous()</c> or a handler's <c>[AllowAnonymous]</c> attribute never appears in
/// the endpoint's own source text, so a scan of the file that maps the route cannot see it. Reading
/// the built <see cref="Microsoft.AspNetCore.Http.Endpoint.Metadata"/> — the same collection the
/// authorization middleware itself reads — sees every route-group, attribute and chain-call form
/// alike, because ASP.NET Core has already merged them by the time the host is running.
/// </summary>
public sealed class IdempotentEndpointMetadata : IIdempotentEndpointMetadata;

public static class IdempotencyEndpointExtensions
{
    /// <summary>
    /// Enables idempotency for this endpoint. Requires Idempotency-Key header on requests.
    /// Duplicate requests with the same key return the cached response.
    /// </summary>
    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddEndpointFilter<IdempotencyEndpointFilter>()
            .WithMetadata(new IdempotentEndpointMetadata());
    }
}
