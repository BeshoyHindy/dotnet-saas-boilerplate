namespace Boilerplate.Modules.Identity.Contracts.DTOs;

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    DateTime AccessTokenExpiresAt);