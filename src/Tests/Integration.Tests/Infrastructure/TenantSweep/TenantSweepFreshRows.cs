using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Tests.Infrastructure.TenantSweep;

/// <summary>
/// Rows minted one at a time for the destructive half of the sweep.
///
/// The positive control for a DELETE has to succeed to be worth anything — and succeeding destroys
/// the row. Reusing the fixture's seeded rows would therefore make the order of the tests part of
/// their meaning. Each destructive control gets its own row instead, created through the same public
/// API, so it can be consumed without consequence.
///
/// Returning <c>null</c> is a deliberate answer, not a gap: some resources (an audit record, a
/// tenant) have no cheap "make me another one" door. Those routes are still probed with tenant B's
/// id — only the destructive control is skipped.
/// </summary>
internal static class TenantSweepFreshRows
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<string?> TryCreateAsync(
        TenantSweepFixture sweep, SeededTenant tenant, ResourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(tenant);

        var unique = Guid.NewGuid().ToString("N")[..8];

        return kind switch
        {
            ResourceKind.Role => await CreateRoleAsync(tenant, unique),
            ResourceKind.Group => await CreateGroupAsync(tenant, unique),
            ResourceKind.File => await CreateFileAsync(tenant, unique),
            _ => null,
        };
    }

    private static async Task<string?> CreateRoleAsync(SeededTenant tenant, string unique)
    {
        using var response = await tenant.AdminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/roles",
            new { id = string.Empty, name = $"sweep-doomed-{unique}", description = "destructive control" });

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<IdDto>(Json);
        return dto?.Id;
    }

    private static async Task<string?> CreateGroupAsync(SeededTenant tenant, string unique)
    {
        using var response = await tenant.AdminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/groups",
            new { name = $"sweep-doomed-{unique}", description = "destructive control", isDefault = false });

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<IdDto>(Json);
        return dto?.Id;
    }

    private static async Task<string?> CreateFileAsync(SeededTenant tenant, string unique)
    {
        using var response = await tenant.AdminClient.PostAsJsonAsync(
            "/api/v1/files/upload-url",
            new
            {
                ownerType = "MyFiles",
                ownerId = (Guid?)null,
                fileName = $"sweep-doomed-{unique}.pdf",
                contentType = "application/pdf",
                sizeBytes = 256,
                visibility = 1,
                category = "Document",
            });

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<FileDto>(Json);
        return dto?.FileAssetId.ToString();
    }

    private sealed record IdDto(string Id);

    private sealed record FileDto(Guid FileAssetId);
}
