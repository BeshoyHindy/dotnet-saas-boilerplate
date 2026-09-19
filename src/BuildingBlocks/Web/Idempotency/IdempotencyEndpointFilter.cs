using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Boilerplate.BuildingBlocks.Caching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Web.Idempotency;

/// <summary>
/// Endpoint filter that provides idempotency for POST/PUT/PATCH requests.
/// When an Idempotency-Key header is present, the response is cached and replayed
/// for subsequent requests with the same key.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="IDistributedCache"/> directly for the probe read (bypassing
/// <see cref="HybridCache"/>'s factory-mandatory API) and <see cref="HybridCache.SetAsync"/>
/// for the write path so replays benefit from L1 and the regular tag invalidation story.
/// Using <c>HybridCache</c> with <c>DisableUnderlyingData</c> as a "get-only probe" is a
/// known anti-pattern tracked at dotnet/aspnetcore#57191.
/// </para>
/// <para>
/// Because the probe talks to L2 directly it has to name the <i>physical</i> key, which the cache
/// now derives from the ambient tenant. It gets that key from <see cref="CacheKeyScope"/> — the same
/// type the cache itself uses — rather than re-deriving the format here, so writer and reader cannot
/// drift apart. (HybridCache 10.x writes L2 under the key it is given, untransformed; the Redis
/// integration tests pin that.)
/// </para>
/// </remarks>
public sealed class IdempotencyEndpointFilter : IEndpointFilter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var options = httpContext.RequestServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value;
        var idempotencyKey = httpContext.Request.Headers[options.HeaderName].ToString();

        // No header = pass through (idempotency is opt-in per request)
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return await next(context).ConfigureAwait(false);
        }

        if (idempotencyKey.Length > options.MaxKeyLength)
        {
            return TypedResults.BadRequest($"Idempotency key exceeds maximum length of {options.MaxKeyLength}.");
        }

        var distributedCache = httpContext.RequestServices.GetRequiredService<IDistributedCache>();
        var keyScope = httpContext.RequestServices.GetRequiredService<CacheKeyScope>();
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<IdempotencyEndpointFilter>>();

        // Which tenant? The resolved one — which covers both the authenticated routes (from the
        // token's claim) and the anonymous tenants/{tenant}/auth ones (from the route value), because
        // this filter runs at the endpoint, after tenant resolution. The old code read the raw
        // "tenant" claim itself and fell back to the literal tenant "global", which put every
        // anonymous and every tenant-less request into one shared partition; both are gone.
        HybridCache cache;
        string logicalKey;
        string cacheKey;
        if (keyScope.HasTenant)
        {
            cache = httpContext.RequestServices.GetRequiredService<HybridCache>();
            logicalKey = CacheKeys.IdempotencyEntry(idempotencyKey);
            // The physical key the write below lands on, asked of the block rather than rebuilt here.
            cacheKey = keyScope.TenantKey(logicalKey);
        }
        else
        {
            // No tenant at all: declare the entry global rather than invent a tenant for it. Nothing
            // in the kit reaches this branch today (all four idempotent endpoints resolve a tenant),
            // so it is the defensive path for a future tenant-less idempotent endpoint.
            cache = httpContext.RequestServices.GetRequiredService<GlobalHybridCache>();
            logicalKey = CacheKeys.GlobalIdempotencyEntry(SubjectId(httpContext), idempotencyKey);
            cacheKey = CacheKeyScope.GlobalKey(logicalKey);
        }

        var tags = new[] { CacheKeys.Tags.Idempotency };

        // Probe-only read via IDistributedCache (real GetAsync, null on miss — unlike HybridCache's
        // factory). Bypasses L1: replays are rare vs first-calls, so L1 warmth has little value.
        var cachedBytes = await distributedCache.GetAsync(cacheKey, httpContext.RequestAborted).ConfigureAwait(false);
        if (cachedBytes is not null && cachedBytes.Length > 0)
        {
            var cached = JsonSerializer.Deserialize<CachedIdempotentResponse>(cachedBytes, JsonOpts);
            if (cached is not null)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Idempotent replay for key {KeyHash}", HashKey(idempotencyKey));
                }
                httpContext.Response.Headers["Idempotency-Replayed"] = "true";
                httpContext.Response.StatusCode = cached.StatusCode;
                if (cached.ContentType is not null)
                {
                    httpContext.Response.ContentType = cached.ContentType;
                }

                if (cached.Body.Length > 0)
                {
                    await httpContext.Response.Body.WriteAsync(cached.Body, httpContext.RequestAborted).ConfigureAwait(false);
                }

                return null; // Response already written
            }
        }

        // Execute the handler
        var result = await next(context).ConfigureAwait(false);

        // Cache the response through HybridCache so the tag invalidation path works for purges.
        try
        {
            var body = result is not null ? JsonSerializer.SerializeToUtf8Bytes(result, JsonOpts) : [];
            var responseToCache = new CachedIdempotentResponse
            {
                StatusCode = httpContext.Response.StatusCode is > 0 and < 600 ? httpContext.Response.StatusCode : 200,
                ContentType = "application/json",
                Body = body
            };

            var setOptions = new HybridCacheEntryOptions
            {
                Expiration = options.DefaultTtl,
                LocalCacheExpiration = options.DefaultTtl < TimeSpan.FromMinutes(2) ? options.DefaultTtl : TimeSpan.FromMinutes(2),
            };
            // Logical key here — the cache applies the same prefix the probe above asked for.
            await cache.SetAsync(logicalKey, responseToCache, setOptions, tags, httpContext.RequestAborted).ConfigureAwait(false);
        }
        // Best-effort caching: idempotency replay is a convenience, not a correctness requirement
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to cache idempotent response for key {KeyHash}", HashKey(idempotencyKey));
        }

        return result;
    }

    /// <summary>
    /// The authenticated subject, used to partition the tenant-less branch. In practice it is always
    /// null there — an authenticated request without a resolvable tenant is rejected with 401 before
    /// any endpoint runs (MultitenancyModule's token-without-tenant guard) — but partitioning by it
    /// costs nothing and keeps the branch honest if that ever changes.
    /// </summary>
    private static string? SubjectId(HttpContext httpContext) =>
        httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? httpContext.User.FindFirst("sub")?.Value;

    private static string HashKey(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}

public static class IdempotencyEndpointExtensions
{
    /// <summary>
    /// Enables idempotency for this endpoint. Requires Idempotency-Key header on requests.
    /// Duplicate requests with the same key return the cached response.
    /// </summary>
    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter<IdempotencyEndpointFilter>();
    }
}
