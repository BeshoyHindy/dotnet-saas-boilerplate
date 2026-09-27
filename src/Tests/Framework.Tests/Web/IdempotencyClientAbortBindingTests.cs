using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace Framework.Tests.Web;

/// <summary>
/// Ticket #104, ported from upstream's <c>IdempotencyCancellationBindingTests</c> (<c>bf86648</c> part
/// c). Minimal-API parameter binding resolves a handler's <see cref="CancellationToken"/> parameter
/// from <see cref="HttpContext.RequestAborted"/> <i>before</i> endpoint filters run, so the value that
/// reaches the handler is whatever <c>RequestAborted</c> was at binding time — reassigning
/// <c>HttpContext.RequestAborted</c> inside the filter does not retroactively change an argument
/// already bound. These run a real host over the real filter for that reason: a hand-built
/// <c>EndpointFilterInvocationContext</c> (as in <see cref="IdempotencyEndpointFilterTests"/>) never
/// goes through binding and cannot see this gap.
/// </summary>
public sealed class IdempotencyClientAbortBindingTests
{
    private const string IdempotencyHeader = "Idempotency-Key";

    [Fact]
    public async Task IdempotentHandler_Should_ReceiveNonCancellableToken_When_FilterDetachesClientAbort()
    {
        CancellationToken observed = default;
        using var host = await StartHostAsync(
            ct =>
            {
                observed = ct;
                return Task.CompletedTask;
            },
            clientAbort: CancellationToken.None);

        using var response = await SendAsync(host, Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        observed.CanBeCanceled.ShouldBeFalse(
            "the handler's bound CancellationToken must be swapped too; reassigning HttpContext." +
            "RequestAborted alone leaves the already-bound argument wired to the client's abort.");
    }

    [Fact]
    public async Task IdempotentHandler_Should_RunToCompletionAndStore_When_ClientAbortIsSignalled()
    {
        // The client hangs up while the handler is running. The handler awaits its own bound token
        // afterwards, so with the token still attached it throws, the filter stores nothing, and the
        // client's retry re-executes a side effect that already committed — that retry is the whole
        // point, so it is what gets asserted, not just that the handler reached its end.
        var executions = 0;
        using var clientGone = new CancellationTokenSource();
        using var host = await StartHostAsync(
            async ct =>
            {
                executions++;
                if (executions == 1)
                {
                    await clientGone.CancelAsync();
                }

                await Task.Delay(20, ct);
            },
            clientGone.Token);

        // The first response never reaches the client, which is correct and not what is under test.
        var key = Guid.NewGuid().ToString("N");
        await Should.ThrowAsync<OperationCanceledException>(() => SendAsync(host, key));

        using var replay = await SendAsync(host, key);

        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        executions.ShouldBe(
            1,
            "the handler ran to completion despite the abort, so its response was stored and the " +
            "retry replayed it instead of committing the side effect a second time.");
    }

    // clientAbort stands in for the connection dropping: a middleware ahead of the endpoint publishes
    // it as RequestAborted, which is what minimal-API binding hands the handler and what the filter has
    // to detach. TestServer has no way to hang up a live request from the client side.
    private static async Task<IHost> StartHostAsync(Func<CancellationToken, Task> handler, CancellationToken clientAbort)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        // The filter resolves CacheKeyScope and IDistributedCache from the request scope. No
        // Multitenancy module here, so singleTenant: true supplies the fixed-partition accessor
        // AddHeroCaching otherwise demands (ADR-0002 — there is no untenanted default).
        builder.Services.AddHeroCaching(builder.Configuration, singleTenant: true);
        builder.Services.AddHeroIdempotency(builder.Configuration);

        var app = builder.Build();
        if (clientAbort.CanBeCanceled)
        {
            // First request only: that is the one whose client hangs up. The retry arrives on a new
            // connection, so publishing the already-cancelled token to it too would fail the replay
            // for a reason that has nothing to do with what is being tested.
            var firstRequest = 1;
            app.Use(async (context, next) =>
            {
                if (Interlocked.Exchange(ref firstRequest, 0) == 1)
                {
                    context.RequestAborted = clientAbort;
                }

                await next(context);
            });
        }

        app.MapPost("/probe", async (CancellationToken ct) =>
        {
            await handler(ct);
            return TypedResults.Ok(new { ok = true });
        }).WithIdempotency();

        // CancellationToken.None: clientAbort stands for the client dropping mid-request, not for a
        // reason to abandon host startup.
        await app.StartAsync(CancellationToken.None);
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(IHost host, string idempotencyKey)
    {
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/probe");
        request.Headers.Add(IdempotencyHeader, idempotencyKey);
        return await client.SendAsync(request);
    }
}
