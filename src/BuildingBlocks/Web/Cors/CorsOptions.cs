namespace Boilerplate.BuildingBlocks.Web.Cors;

public sealed class CorsOptions
{
    public bool AllowAll { get; init; } = true;
    public string[] AllowedOrigins { get; init; } = [];

    // Empty, not ["*"]: the configuration binder appends configured array entries to whatever the
    // property already holds instead of replacing it, so a ["*"] default here would survive
    // alongside a restricted host's configured list (e.g. ["*", "content-type", "authorization"]) —
    // the wildcard silently outlives the restriction it was supposed to be replaced by. AllowAll's
    // branch never reads these two, so the empty default changes nothing for it; the AllowAll=false
    // branch already requires both non-empty via the .Validate calls in AddAppCors, so a caller who
    // configures nothing there was never a supported "leave it default" case.
    public string[] AllowedHeaders { get; init; } = [];
    public string[] AllowedMethods { get; init; } = [];
}