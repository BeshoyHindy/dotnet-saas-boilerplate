namespace Integration.Middleware.Tests.Infrastructure;

/// <summary>What the pipeline decided the request looks like, as seen by an endpoint.</summary>
public sealed record RequestInfo(string Host, string Scheme);

/// <summary>Shape of the /health payload (mirrors HealthEndpoints.HealthResult).</summary>
public sealed record HealthPayload(string Status, IReadOnlyList<HealthPayloadEntry> Results);

public sealed record HealthPayloadEntry(string Name, string Status);

public sealed class TokenResult
{
    public string AccessToken { get; set; } = default!;
    public string RefreshToken { get; set; } = default!;
    public DateTime RefreshTokenExpiresAt { get; set; }
    public DateTime AccessTokenExpiresAt { get; set; }
}
