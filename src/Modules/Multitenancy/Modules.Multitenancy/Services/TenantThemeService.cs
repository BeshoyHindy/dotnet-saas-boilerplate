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

    private readonly HybridCache _cache;
    private readonly TenantDbContext _dbContext;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _tenantAccessor;
    private readonly IStorageService _storageService;
    private readonly ILogger<TenantThemeService> _logger;
    private readonly ICurrentUser _currentUser;

    public TenantThemeService(
        HybridCache cache,
        TenantDbContext dbContext,
        IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
        IStorageService storageService,
        ILogger<TenantThemeService> logger,
        ICurrentUser currentUser)
    {
        _cache = cache;
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
        return _cache.GetOrCreateAsync(
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

    public async Task UpdateThemeAsync(string tenantId, TenantThemeDto theme, CancellationToken ct = default)
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

    private async Task HandleBrandAssetUploadsAsync(BrandAssetsDto assets, TenantTheme entity, CancellationToken ct)
    {
        // Handle logo upload (same pattern as profile picture)
        if (assets.Logo?.Data is { Count: > 0 })
        {
            var oldLogoUrl = entity.LogoUrl;
            entity.LogoUrl = await _storageService.UploadAsync<TenantTheme>(assets.Logo, FileType.Image, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(oldLogoUrl))
            {
                await _storageService.RemoveAsync(oldLogoUrl, ct).ConfigureAwait(false);
            }
        }
        else if (assets.DeleteLogo && !string.IsNullOrEmpty(entity.LogoUrl))
        {
            await _storageService.RemoveAsync(entity.LogoUrl, ct).ConfigureAwait(false);
            entity.LogoUrl = null;
        }

        // Handle logo dark upload
        if (assets.LogoDark?.Data is { Count: > 0 })
        {
            var oldLogoUrl = entity.LogoDarkUrl;
            entity.LogoDarkUrl = await _storageService.UploadAsync<TenantTheme>(assets.LogoDark, FileType.Image, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(oldLogoUrl))
            {
                await _storageService.RemoveAsync(oldLogoUrl, ct).ConfigureAwait(false);
            }
        }
        else if (assets.DeleteLogoDark && !string.IsNullOrEmpty(entity.LogoDarkUrl))
        {
            await _storageService.RemoveAsync(entity.LogoDarkUrl, ct).ConfigureAwait(false);
            entity.LogoDarkUrl = null;
        }

        // Handle favicon upload
        if (assets.Favicon?.Data is { Count: > 0 })
        {
            var oldFaviconUrl = entity.FaviconUrl;
            entity.FaviconUrl = await _storageService.UploadAsync<TenantTheme>(assets.Favicon, FileType.Image, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(oldFaviconUrl))
            {
                await _storageService.RemoveAsync(oldFaviconUrl, ct).ConfigureAwait(false);
            }
        }
        else if (assets.DeleteFavicon && !string.IsNullOrEmpty(entity.FaviconUrl))
        {
            await _storageService.RemoveAsync(entity.FaviconUrl, ct).ConfigureAwait(false);
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

        EnsureAmbient(tenantId);

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

        // Invalidate default theme cache
        await _cache.RemoveAsync(CacheKeys.DefaultTheme, ct).ConfigureAwait(false);

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

    private static void MapDtoToEntity(TenantThemeDto dto, TenantTheme entity)
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

        // Brand Assets - URLs are handled by HandleBrandAssetUploadsAsync
        // Only copy URL if it's a real URL (not a data URL preview)
        if (!string.IsNullOrEmpty(dto.BrandAssets.LogoUrl) && !dto.BrandAssets.LogoUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            entity.LogoUrl = dto.BrandAssets.LogoUrl;
        }
        if (!string.IsNullOrEmpty(dto.BrandAssets.LogoDarkUrl) && !dto.BrandAssets.LogoDarkUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            entity.LogoDarkUrl = dto.BrandAssets.LogoDarkUrl;
        }
        if (!string.IsNullOrEmpty(dto.BrandAssets.FaviconUrl) && !dto.BrandAssets.FaviconUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            entity.FaviconUrl = dto.BrandAssets.FaviconUrl;
        }

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