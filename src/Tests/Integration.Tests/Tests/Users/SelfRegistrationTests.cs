using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Users;

[Collection(AppCollectionDefinition.Name)]
public sealed class SelfRegistrationTests
{
    private readonly AppWebApplicationFactory _factory;

    public SelfRegistrationTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SelfRegister_Should_Return201_When_AnonymousAndPayloadIsValid()
    {
        using var client = _factory.CreateClient();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync($"{TestConstants.RootAuthBasePath}/register", new
        {
            firstName = "Self",
            lastName = "Reg",
            email = $"self-{uniqueId}@example.com",
            userName = $"selfreg-{uniqueId}",
            password = "Test@1234!",
            confirmPassword = "Test@1234!"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var result = await response.DeserializeAsync<RegisterResult>();
        result.UserId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SelfRegister_Should_Return400_When_PayloadInvalid()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{TestConstants.RootAuthBasePath}/register", new
        {
            firstName = "",
            lastName = "",
            email = "not-an-email",
            userName = "",
            password = "weak",
            confirmPassword = "weak"
        });

        response.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task SelfRegister_Should_RejectDuplicate_When_EmailAlreadyExists()
    {
        using var client = _factory.CreateClient();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var payload = new
        {
            firstName = "Dup",
            lastName = "Self",
            email = $"selfdup-{uniqueId}@example.com",
            userName = $"selfdup-{uniqueId}",
            password = "Test@1234!",
            confirmPassword = "Test@1234!"
        };

        var firstResponse = await client.PostAsJsonAsync(
            $"{TestConstants.RootAuthBasePath}/register", payload);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var secondResponse = await client.PostAsJsonAsync(
            $"{TestConstants.RootAuthBasePath}/register",
            payload with { userName = $"selfdup2-{uniqueId}" });

        secondResponse.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task SelfRegister_Should_NotReplay_When_RepeatedWithSameIdempotencyKey()
    {
        // Anonymous routes are never marked idempotent (#84): the same Idempotency-Key header must
        // not replay the first response. The second attempt is refused as a duplicate (400), the same
        // as it would be without the header at all.
        using var client = _factory.CreateClient();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var payload = new
        {
            firstName = "Idem",
            lastName = "Self",
            email = $"selfidem-{uniqueId}@example.com",
            userName = $"selfidem-{uniqueId}",
            password = "Test@1234!",
            confirmPassword = "Test@1234!"
        };
        var idempotencyKey = $"self-register-{uniqueId}";

        using var firstRequest = BuildRequest(payload, idempotencyKey);
        var firstResponse = await client.SendAsync(firstRequest);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var secondRequest = BuildRequest(payload, idempotencyKey);
        var secondResponse = await client.SendAsync(secondRequest);

        secondResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        secondResponse.Headers.Contains("Idempotency-Replayed").ShouldBeFalse();
    }

    private static HttpRequestMessage BuildRequest(object payload, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.RootAuthBasePath}/register")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }
}
