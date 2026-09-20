namespace Boilerplate.Modules.Files.Contracts.v1.DTOs;

public sealed record PresignedDownloadResponse(Uri Url, DateTimeOffset ExpiresAt);
