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

    // Full replay-with-matching-body coverage isn't possible yet (filter captures the raw IResult, not the body — dotnet/aspnetcore#57191, backlog 2.4b).
    // These tests verify only the wiring: Idempotency-Replayed header presence/absence and that a distinct key forces fresh execution.

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
