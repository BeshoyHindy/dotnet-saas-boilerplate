using System.Diagnostics;
using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.Modules.Identity.Contracts.Events;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Eventing;

/// <summary>
/// An integration event carries the trace id of the request that published it as its
/// <c>CorrelationId</c> — not a fresh GUID that appears nowhere else — so the event joins back to
/// that request's spans, log lines and audit rows.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class IntegrationEventCorrelationTests
{
    private readonly AppWebApplicationFactory _factory;

    public IntegrationEventCorrelationTests(AppWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task An_Event_Published_By_A_Request_Should_Carry_That_Requests_Trace_Id()
    {
        // OpenTelemetry is off in the test host; without a listener ASP.NET Core starts no request
        // Activity and the incoming traceparent is never adopted.
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var spanId = ActivitySpanId.CreateRandom().ToHexString();

        // Issuing a token publishes TokenGeneratedIntegrationEvent through the outbox.
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{TestConstants.RootAuthBasePath}/token");
        request.Headers.Add("traceparent", $"00-{traceId}-{spanId}-01");
        request.Content = JsonContent.Create(
            new { email = TestConstants.RootAdminEmail, password = TestConstants.DefaultPassword });

        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        var published = await eventing.OutboxMessages
            .Where(m => m.CorrelationId == traceId)
            .Select(m => m.Type)
            .ToListAsync();

        published.ShouldContain(
            t => t.Contains(nameof(TokenGeneratedIntegrationEvent), StringComparison.Ordinal),
            "the token request's event must carry the request's trace id as its correlation id");
    }
}
