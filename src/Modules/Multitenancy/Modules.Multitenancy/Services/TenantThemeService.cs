using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Boilerplate.Modules.Multitenancy.Data;
using Boilerplate.Modules.Multitenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Multitenancy.Services;

public sealed class TenantThemeService : ITenantThemeService
{
    private static readonly HybridCacheEntryOptions ThemeEntryOptions = new()
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(2),
    };

    // One shared array for every theme entry. It used to be allocated per call because it carried a
    // per-tenant tag; the cache now scopes tags to the ambient tenant itself, so the tag is a constant.
    private static readonly string[] ThemeTags = [CacheKeys.Tags.Themes];

    // The owner segment each brand asset is stored under, inside the tenant's public space (#83).
    // Slot-level, not tenant-level, so replacing the logo can only delete a previous logo — never the
    // favicon, and never a user's avatar. These are storage *owner* names, not key roots: the block
    // still composes the key.
    private const string LogoOwner = "logo";
    private const string LogoDarkOwner = "logo-dark";
    private const string FaviconOwner = "favicon";

    private readonly HybridCache _cache;
    private readonly GlobalHybridCache _globalCache;
    private readonly TenantDbContext _dbContext;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _tenantAccessor;
    private readonly IStorageService _storageService;
    private readonly ILogger<TenantThemeService> _logger;
    private readonly ICurrentUser _currentUser;

    public TenantThemeService(
        HybridCache cache,
        GlobalHybridCache globalCache,
        TenantDbContext dbContext,
        IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
        IStorageService storageService,
        ILogger<TenantThemeService> logger,
        ICurrentUser currentUser)
    {
        _cache = cache;
        _globalCache = globalCache;
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _storageService = storageService;
        _logger = logger;
        _currentUser = currentUser;
    }

    public async Task<TenantThemeDto> GetCurrentTenantThemeAsync(CancellationToken ct = default)
    {
        var tenantId = _tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new InvalidOperationException("No tenant context available");
        return await GetThemeAsync(tenantId, ct).ConfigureAwait(false);
    }

    public Task<TenantThemeDto> GetThemeAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        EnsureAmbient(tenantId);

        // Stateless factory via a static method group — no closure allocation even on L1 hits.
        var state = new TenantFactoryState(_dbContext, tenantId);
        return _cache.GetOrCreateAsync(
            CacheKeys.TenantTheme,
            state,
            LoadTenantThemeAsync,
            ThemeEntryOptions,
            ThemeTags,
            ct).AsTask();
    }

    public Task<TenantThemeDto> GetDefaultThemeAsync(CancellationToken ct = default)
    {
        // The default-theme row is a single, platform-wide record in the unfiltered tenant-catalog
        // context — not a per-tenant query — so it goes through GlobalHybridCache. Caching it under
        // the ambient tenant would file one shared answer under every tenant's own partition, and
        // invalidating it (SetAsDefaultThemeAsync) would only ever clear the caller's copy.
        return _globalCache.GetOrCreateAsync(
            CacheKeys.DefaultTheme,
            _dbContext,
            LoadDefaultThemeAsync,
            ThemeEntryOptions,
            ThemeTags,
            ct).AsTask();
    }

    /// <summary>
    /// Every method here takes the tenant it operates on, and every caller passes the ambient one —
    /// the theme rows are behind the tenant query filter, so another tenant's row is invisible
    /// anyway. Since #77 the cache key is derived from the ambient tenant rather than from this
    /// argument, so the two must agree or the entry would be filed under the wrong tenant. Rather
    /// than let that drift, say so: crossing to another tenant is <c>ITenantScope.RunAsync</c>, the
    /// one mechanism for it (ADR-0002), not an argument.
    /// </summary>
    private void EnsureAmbient(string tenantId)
    {
        var ambient = _tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
        if (!string.Equals(ambient, tenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Theme operations run inside their own tenant. Ambient tenant is '{ambient ?? "(none)"}' " +
                $"but '{tenantId}' was requested — enter that tenant with ITenantScope.RunAsync first.");
        }
    }

    private static async ValueTask<TenantThemeDto> LoadTenantThemeAsync(TenantFactoryState state, CancellationToken ct)
    {
        var entity = await state.DbContext.TenantThemes
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == state.TenantId, ct)
            .ConfigureAwait(false);

        return entity is null ? TenantThemeDto.Default : MapEntityToDto(entity);
    }

    private static async ValueTask<TenantThemeDto> LoadDefaultThemeAsync(TenantDbContext dbContext, CancellationToken ct)
    {
        var entity = await dbContext.TenantThemes
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.IsDefault, ct)
            .ConfigureAwait(false);

        return entity is null ? TenantThemeDto.Default : MapEntityToDto(entity);
    }

    private readonly record struct TenantFactoryState(TenantDbContext DbContext, string TenantId);

    public async Task UpdateThemeAsync(string tenantId, TenantThemeUpdateDto theme, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(theme);
        EnsureAmbient(tenantId);

        var entity = await _dbContext.TenantThemes
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = TenantTheme.Create(tenantId);
            _dbContext.TenantThemes.Add(entity);
        }

        // Handle brand asset uploads
        await HandleBrandAssetUploadsAsync(theme.BrandAssets, entity, ct).ConfigureAwait(false);

        MapDtoToEntity(theme, entity);
        entity.Update(GetCurrentUserId());

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await InvalidateCacheAsync(tenantId, ct).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Updated theme for tenant {TenantId}", tenantId);
        }
    }

    /// <remarks>
    /// <b>Each slot is its own owner.</b> An asset is uploaded under the slot it is for
    /// (<see cref="LogoOwner"/>, <see cref="LogoDarkOwner"/>, <see cref="FaviconOwner"/>) and the
    /// value it replaces is dropped with the owner-scoped <c>RemoveIfOwnedAsync&lt;TenantTheme&gt;</c>,
    /// which deletes only an object a previous upload <i>for that same slot</i> produced (#83). The
    /// tenant-wide check is not enough on its own: a user's avatar is a key this tenant owns too, so
    /// before the URL input was removed a tenant admin could park one in <c>LogoUrl</c> and have the
    /// next save delete someone's face.
    ///
    /// <para>What that skips over is exactly what exists in the wild: a column written when the
    /// editor still accepted a pasted address, and a development database predating tenant-prefixed
    /// keys that still holds flat <c>uploads/{type}/…</c> values. Both are logged and left alone
    /// rather than failing the whole theme save, and the column is then overwritten with a
    /// server-issued value.</para>
    ///
    /// <para>The tenant this writes for is always the ambient one — the endpoint takes no tenant
    /// and a root operator editing another tenant's branding arrives on an exchanged token whose
    /// <c>tenant</c> claim is the target (ADR-0002) — so the keys the block composes below land in
    /// the right place without a tenant parameter anywhere in the storage API.</para>
    /// </remarks>
    private async Task HandleBrandAssetUploadsAsync(BrandAssetUploadsDto assets, TenantTheme entity, CancellationToken ct)
    {
        // Handle logo upload (same pattern as profile picture)
        if (assets.Logo?.Data is { Count: > 0 })
        {
            var oldLogoUrl = entity.LogoUrl;
            entity.LogoUrl = await _storageService.UploadAsync<TenantTheme>(assets.Logo, FileType.Image, LogoOwner, ct).ConfigureAwait(false);
            await _storageService.RemoveIfOwnedAsync<TenantTheme>(oldLogoUrl, LogoOwner, ct).ConfigureAwait(false);
        }
        else if (assets.DeleteLogo && !string.IsNullOrEmpty(entity.LogoUrl))
        {
            await _storageService.RemoveIfOwnedAsync<TenantTheme>(entity.LogoUrl, LogoOwner, ct).ConfigureAwait(false);
            entity.LogoUrl = null;
        }

        // Handle logo dark upload
        if (assets.LogoDark?.Data is { Count: > 0 })
        {
            var oldLogoUrl = entity.LogoDarkUrl;
            entity.LogoDarkUrl = await _storageService.UploadAsync<TenantTheme>(assets.LogoDark, FileType.Image, LogoDarkOwner, ct).ConfigureAwait(false);
            await _storageService.RemoveIfOwnedAsync<TenantTheme>(oldLogoUrl, LogoDarkOwner, ct).ConfigureAwait(false);
        }
        else if (assets.DeleteLogoDark && !string.IsNullOrEmpty(entity.LogoDarkUrl))
        {
            await _storageService.RemoveIfOwnedAsync<TenantTheme>(entity.LogoDarkUrl, LogoDarkOwner, ct).ConfigureAwait(false);
            entity.LogoDarkUrl = null;
        }

        // Handle favicon upload
        if (assets.Favicon?.Data is { Count: > 0 })
        {
            var oldFaviconUrl = entity.FaviconUrl;
            entity.FaviconUrl = await _storageService.UploadAsync<TenantTheme>(assets.Favicon, FileType.Image, FaviconOwner, ct).ConfigureAwait(false);
            await _storageService.RemoveIfOwnedAsync<TenantTheme>(oldFaviconUrl, FaviconOwner, ct).ConfigureAwait(false);
        }
        else if (assets.DeleteFavicon && !string.IsNullOrEmpty(entity.FaviconUrl))
        {
            await _storageService.RemoveIfOwnedAsync<TenantTheme>(entity.FaviconUrl, FaviconOwner, ct).ConfigureAwait(false);
            entity.FaviconUrl = null;
        }
    }

    public async Task ResetThemeAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        EnsureAmbient(tenantId);

        var entity = await _dbContext.TenantThemes
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (entity is not null)
        {
            entity.ResetToDefaults();
            entity.Update(GetCurrentUserId());
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await InvalidateCacheAsync(tenantId, ct).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Reset theme to defaults for tenant {TenantId}", tenantId);
        }
    }

    public async Task SetAsDefaultThemeAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Ensure only root tenant can set default theme
        var currentTenantId = _tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
        if (currentTenantId != MultitenancyConstants.Root.Id)
        {
            throw new ForbiddenException("Only the root tenant can set the default theme");
        }

        // No EnsureAmbient here: root nominates *another* tenant's theme as the default, so tenantId
        // is legitimately not the ambient one. The root-only check above is what establishes the
        // caller; EnsureAmbient stays on the per-tenant methods, where the argument and the ambient
        // tenant really must agree.

        // Clear existing default
        var existingDefault = await _dbContext.TenantThemes
            .FirstOrDefaultAsync(t => t.IsDefault, ct)
            .ConfigureAwait(false);

        if (existingDefault is not null)
        {
            existingDefault.IsDefault = false;
        }

        // Set new default
        var entity = await _dbContext.TenantThemes
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            throw new NotFoundException($"Theme for tenant {tenantId} not found");
        }

        entity.IsDefault = true;
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        // Invalidate the default theme cache. This is a global entry (see GetDefaultThemeAsync), so
        // the global cache is what has to clear it — going through the tenant-scoped cache would only
        // ever evict the root tenant's own copy, leaving everyone else reading the stale default.
        await _globalCache.RemoveAsync(CacheKeys.DefaultTheme, ct).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Set theme for tenant {TenantId} as default", tenantId);
        }
    }

    public async Task InvalidateCacheAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        EnsureAmbient(tenantId);

        // Purge the tenant's own theme entry plus anything else it has tagged as a theme (its cached
        // default-theme answer). Both calls are scoped to the ambient tenant by the cache, so this
        // can no longer evict another tenant's themes the way the old `tenant:{id}` tag could.
        await _cache.RemoveAsync(CacheKeys.TenantTheme, ct).ConfigureAwait(false);
        await _cache.RemoveByTagAsync(CacheKeys.Tags.Themes, ct).ConfigureAwait(false);
    }

    private static TenantThemeDto MapEntityToDto(TenantTheme entity)
    {
        return new TenantThemeDto
        {
            LightPalette = new PaletteDto
            {
                Primary = entity.PrimaryColor,
                Secondary = entity.SecondaryColor,
                Tertiary = entity.TertiaryColor,
                Background = entity.BackgroundColor,
                Surface = entity.SurfaceColor,
                Error = entity.ErrorColor,
                Warning = entity.WarningColor,
                Success = entity.SuccessColor,
                Info = entity.InfoColor
            },
            DarkPalette = new PaletteDto
            {
                Primary = entity.DarkPrimaryColor,
                Secondary = entity.DarkSecondaryColor,
                Tertiary = entity.DarkTertiaryColor,
                Background = entity.DarkBackgroundColor,
                Surface = entity.DarkSurfaceColor,
                Error = entity.DarkErrorColor,
                Warning = entity.DarkWarningColor,
                Success = entity.DarkSuccessColor,
                Info = entity.DarkInfoColor
            },
            BrandAssets = new BrandAssetsDto
            {
                LogoUrl = entity.LogoUrl,
                LogoDarkUrl = entity.LogoDarkUrl,
                FaviconUrl = entity.FaviconUrl
            },
            Typography = new TypographyDto
            {
                FontFamily = entity.FontFamily,
                HeadingFontFamily = entity.HeadingFontFamily,
                FontSizeBase = entity.FontSizeBase,
                LineHeightBase = entity.LineHeightBase
            },
            Layout = new LayoutDto
            {
                BorderRadius = entity.BorderRadius,
                DefaultElevation = entity.DefaultElevation
            },
            IsDefault = entity.IsDefault
        };
    }

    /// <summary>
    /// Copies the writable half of a theme onto the entity. <b>The brand-asset columns are not in
    /// it</b>: they are written only by <see cref="HandleBrandAssetUploadsAsync"/>, from what the
    /// Storage block returned. This used to copy <c>dto.BrandAssets.LogoUrl</c> and friends straight
    /// across — with a <c>data:</c>-prefix check as the only filter — which is what let a client
    /// name any URL at all, including another object of this tenant's (#83). The write model no
    /// longer carries those fields, so there is nothing here to copy.
    /// </summary>
    private static void MapDtoToEntity(TenantThemeUpdateDto dto, TenantTheme entity)
    {
        // Light Palette
        entity.PrimaryColor = dto.LightPalette.Primary;
        entity.SecondaryColor = dto.LightPalette.Secondary;
        entity.TertiaryColor = dto.LightPalette.Tertiary;
        entity.BackgroundColor = dto.LightPalette.Background;
        entity.SurfaceColor = dto.LightPalette.Surface;
        entity.ErrorColor = dto.LightPalette.Error;
        entity.WarningColor = dto.LightPalette.Warning;
        entity.SuccessColor = dto.LightPalette.Success;
        entity.InfoColor = dto.LightPalette.Info;

        // Dark Palette
        entity.DarkPrimaryColor = dto.DarkPalette.Primary;
        entity.DarkSecondaryColor = dto.DarkPalette.Secondary;
        entity.DarkTertiaryColor = dto.DarkPalette.Tertiary;
        entity.DarkBackgroundColor = dto.DarkPalette.Background;
        entity.DarkSurfaceColor = dto.DarkPalette.Surface;
        entity.DarkErrorColor = dto.DarkPalette.Error;
        entity.DarkWarningColor = dto.DarkPalette.Warning;
        entity.DarkSuccessColor = dto.DarkPalette.Success;
        entity.DarkInfoColor = dto.DarkPalette.Info;

        // Typography
        entity.FontFamily = dto.Typography.FontFamily;
        entity.HeadingFontFamily = dto.Typography.HeadingFontFamily;
        entity.FontSizeBase = dto.Typography.FontSizeBase;
        entity.LineHeightBase = dto.Typography.LineHeightBase;

        // Layout
        entity.BorderRadius = dto.Layout.BorderRadius;
        entity.DefaultElevation = dto.Layout.DefaultElevation;
    }

    private string? GetCurrentUserId()
    {
        var userId = _currentUser.GetUserId();
        return userId == Guid.Empty ? null : userId.ToString();
    }
}