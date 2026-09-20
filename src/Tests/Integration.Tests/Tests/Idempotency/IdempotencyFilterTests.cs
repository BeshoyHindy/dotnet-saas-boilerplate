using System.Net.Http.Json;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Idempotency;

[Collection(AppCollectionDefinition.Name)]
public sealed class IdempotencyFilterTests
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string ReplayedHeader = "Idempotency-Replayed";
    private const string RegisterUserPath = $"{TestConstants.IdentityBasePath}/register";

    private readonly AuthHelper _auth;

    public IdempotencyFilterTests(AppWebApplicationFactory factory)
    {
        _auth = new AuthHelper(factory);
    }

    // Replay is observable here now (#82). The filter keeps its own entries in IDistributedCache,
    // which this host has — an in-memory one, which works perfectly well when used directly; it is
    // HybridCache that ignores it as an L2. It also captures the executed response rather than the
    // raw IResult, so "the second response is the first response" is an assertion about the body.
    // Replay against a real Redis is IdempotencyRedisReplayTests.

    [Fact]
    public async Task RegisterUser_Should_ExecuteNormally_When_NoIdempotencyKey()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        using var request = new HttpRequestMessage(HttpMethod.Post, RegisterUserPath)
        {
            Content = JsonContent.Create(NewUserPayload(uniqueId))
        };
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Contains(ReplayedHeader).ShouldBeFalse(
            "A request without an idempotency key must never be marked as replayed.");
    }

    [Fact]
    public async Task RegisterUser_Should_ExecuteSecondCall_When_DifferentIdempotencyKey()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var firstKey = $"idem-a-{uniqueId}";
        var secondKey = $"idem-b-{uniqueId}";

        using var firstRequest = BuildRequest(NewUserPayload($"a{uniqueId}"), firstKey);
        (await client.SendAsync(firstRequest)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Different key + a different user so it's a legitimately new resource.
        using var secondRequest = BuildRequest(NewUserPayload($"b{uniqueId}"), secondKey);
        var secondResponse = await client.SendAsync(secondRequest);

        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        secondResponse.Headers.Contains(ReplayedHeader).ShouldBeFalse(
            "A different idempotency key must route through a fresh execution.");
    }

    [Fact]
    public async Task RegisterUser_Should_ReplayTheFirstResponse_When_SameIdempotencyKey()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var payload = NewUserPayload(uniqueId);
        var key = $"idem-replay-{uniqueId}";

        using var firstRequest = BuildRequest(payload, key);
        var first = await client.SendAsync(firstRequest);
        using var secondRequest = BuildRequest(payload, key);
        var second = await client.SendAsync(secondRequest);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        first.Headers.Contains(ReplayedHeader).ShouldBeFalse();

        // Without replay this is a 400: the user already exists. Getting the first response back,
        // byte for byte, is the whole contract.
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.Contains(ReplayedHeader).ShouldBeTrue();
        (await second.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RegisterUser_Should_Refuse_When_SameKey_Carries_A_DifferentPayload()
    {
        // Replaying here would answer a request nobody made — the caller asked to create a different
        // user. 422 is the IETF Idempotency-Key draft's answer for a key reused with a new payload.
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var key = $"idem-conflict-{uniqueId}";

        using var firstRequest = BuildRequest(NewUserPayload($"a{uniqueId}"), key);
        (await client.SendAsync(firstRequest)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var secondRequest = BuildRequest(NewUserPayload($"b{uniqueId}"), key);
        var second = await client.SendAsync(secondRequest);

        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        second.Headers.Contains(ReplayedHeader).ShouldBeFalse();
    }

    private static object NewUserPayload(string uniqueId) => new
    {
        firstName = "Idem",
        lastName = "Potency",
        email = $"idem-{uniqueId}@example.com",
        userName = $"idem{uniqueId}",
        password = "Sup3rStr0ng!Pass",
        confirmPassword = "Sup3rStr0ng!Pass"
    };

    private static HttpRequestMessage BuildRequest(object payload, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RegisterUserPath)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add(IdempotencyHeader, idempotencyKey);
        return request;
    }
}
