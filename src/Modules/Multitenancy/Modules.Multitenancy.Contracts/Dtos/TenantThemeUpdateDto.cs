using Boilerplate.BuildingBlocks.Shared.Storage;

namespace Boilerplate.Modules.Multitenancy.Contracts.Dtos;

/// <summary>
/// What a client may <b>write</b> to a tenant's theme. Deliberately not <see cref="TenantThemeDto"/>:
/// that one is the read model and carries the brand-asset URLs, which a client may not set (#83).
///
/// <para>Before this split the same record served both directions, so <c>logoUrl</c> was an input —
/// any string became the tenant's logo, including the URL of a user's avatar in the same tenant,
/// which the next replace or delete would then remove. Splitting the two is what makes "the server
/// persists only values it produced" a property of the contract rather than a rule the handler has
/// to remember: there is no field here to put a URL in.</para>
/// </summary>
public sealed record TenantThemeUpdateDto
{
    public PaletteDto LightPalette { get; init; } = new();
    public PaletteDto DarkPalette { get; init; } = new();
    public BrandAssetUploadsDto BrandAssets { get; init; } = new();
    public TypographyDto Typography { get; init; } = new();
    public LayoutDto Layout { get; init; } = new();
}

/// <summary>
/// The write side of the brand assets: bytes to upload, or a flag to remove what is stored. Each
/// slot is its own owner inside the tenant (see <c>TenantThemeService</c>), so replacing the logo can
/// only ever delete an object a previous logo upload produced.
/// </summary>
public sealed record BrandAssetUploadsDto
{
    public FileUploadRequest? Logo { get; init; }
    public FileUploadRequest? LogoDark { get; init; }
    public FileUploadRequest? Favicon { get; init; }

    public bool DeleteLogo { get; init; }
    public bool DeleteLogoDark { get; init; }
    public bool DeleteFavicon { get; init; }
}
