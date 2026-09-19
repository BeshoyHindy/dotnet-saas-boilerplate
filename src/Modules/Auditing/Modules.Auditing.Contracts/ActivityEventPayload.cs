namespace Boilerplate.Modules.Auditing.Contracts;

public sealed record ActivityEventPayload(
    ActivityKind Kind,
    string Name,                 // route template, command/query name, job id
    int? StatusCode,
    int DurationMs,
    BodyCapture Captured,        // Request/Response/Both/None
    int RequestSize,
    int ResponseSize,
    object? RequestPreview,      // truncated/filtered snapshot (JSON-friendly)
    object? ResponsePreview,
    // RFC 8693 actor claims, set only when the request carried a token that acts as somebody else
    // (same-tenant impersonation or an exchanged operator token). The envelope's UserId is the
    // *subject being acted as*; without these the trail cannot answer "who actually did this".
    string? ActorSubject = null,
    string? ActorTenant = null
);