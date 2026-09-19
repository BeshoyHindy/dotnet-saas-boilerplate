using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Storage.Keys;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Data;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Integration.Tests.Tests.Storage;

/// <summary>
/// The storage half of tenant isolation, on real MinIO (ADR-0002, #78): the object <b>key</b> is
/// tenant-prefixed by the Storage block, and the block refuses a key the ambient tenant does not
/// own before it reaches the backend.
///
/// <para><b>Why these probes are at the service level.</b> No endpoint accepts a raw storage key —
/// every handler looks the key up from a tenant-filtered row, which is exactly the design and is
/// already swept by <c>TenantEndpointSweepTests</c> and <c>FileTenantIsolationTests</c> (tenant B
/// asking for A's file id gets 404). So the interesting question — "what if a key <i>did</i> reach
/// the block from the wrong tenant" — has no HTTP surface to ask it through. It is asked here
/// instead: tenant B's object is seeded through the real endpoints, its real key is read out of
/// tenant B's own database, and every storage operation is then invoked as tenant A under
/// <see cref="ITenantScope"/> — the same way a job enters a tenant.</para>
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class StorageTenantIsolationTests : IAsyncLifetime
{
    private const string FilesBasePath = "/api/v1/files";
    private const string ThemePath = $"{TestConstants.TenantsBasePath}/theme";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;
    private readonly TenantFixtures _tenants;

    private string _tenantA = default!;
    private string _tenantAAdmin = default!;
    private string _tenantB = default!;
    private string _tenantBAdmin = default!;

    public StorageTenantIsolationTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
        _tenants = new TenantFixtures(factory);
    }

    public async Task InitializeAsync()
    {
        (_tenantA, _tenantAAdmin) = await _tenants.CreateProvisionedTenantAsync("stg-a");
        (_tenantB, _tenantBAdmin) = await _tenants.CreateProvisionedTenantAsync("stg-b");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    #region Cross-tenant refusal on a real key

    [Fact]
    public async Task TenantA_Should_Be_Refused_Every_Operation_On_TenantBs_Private_Key()
    {
        // Arrange — tenant B uploads a file the normal way, and we read the key its row holds.
        using var clientB = await ClientForAsync(_tenantB, _tenantBAdmin);
        var fileId = await UploadAndFinalizeAsync(clientB, "b-secret.pdf", "application/pdf", RandomBytes(512));
        var key = await StorageKeyOfAsync(_tenantB, fileId);

        key.ShouldStartWith($"tenants/{_tenantB}/");

        // Act & Assert — as tenant A, every entry point that takes a key refuses this one.
        await AsTenantAsync(_tenantA, async storage =>
        {
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.DownloadAsync(key));
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.ExistsAsync(key));
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.GetSizeAsync(key));
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.HeadObjectAsync(key));
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.RemoveAsync(key));
            await Should.ThrowAsync<StorageKeyNotOwnedException>(
                () => storage.GenerateDownloadUrlAsync(key, TimeSpan.FromMinutes(5)));
            await Should.ThrowAsync<StorageKeyNotOwnedException>(
                () => storage.GenerateUploadUrlAsync(key, "application/pdf", 1024, TimeSpan.FromMinutes(5)));

            // The best-effort delete skips rather than throws — and still deletes nothing.
            (await storage.RemoveIfOwnedAsync(key)).ShouldBeFalse();
        });

        // Assert — and tenant B's bytes are exactly where they were.
        await AsTenantAsync(_tenantB, async storage =>
            (await storage.ExistsAsync(key)).ShouldBeTrue("tenant A's refused calls must not have deleted anything"));

        using var stillThere = await clientB.GetAsync($"{FilesBasePath}/{fileId}/url");
        stillThere.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TenantA_Should_Be_Refused_TenantBs_PublicKey_And_Its_Persisted_Url()
    {
        // The public space is prefixed too: `uploads/` is shared, the tenant segment under it is not.
        using var clientB = await ClientForAsync(_tenantB, _tenantBAdmin);
        var avatarUrl = await UploadAvatarAsync(clientB, "b-avatar.png");
        var key = KeyFromPublicUrl(avatarUrl);

        key.ShouldStartWith($"uploads/tenants/{_tenantB}/");

        await AsTenantAsync(_tenantA, async storage =>
        {
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.ExistsAsync(key));
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.RemoveAsync(key));
            // Handing over the persisted URL instead of the key changes nothing: mapping it back to
            // a key is the block's job, and the answer is the same.
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.RemoveAsync(avatarUrl));
            Should.Throw<StorageKeyNotOwnedException>(() => storage.BuildPublicUrl(key));
        });

        (await FetchAnonymouslyAsync(avatarUrl)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TwoTenants_Should_Get_Different_Keys_For_The_Same_Upload()
    {
        // Same file name, same owner type, same everything a caller controls — different objects.
        using var clientA = await ClientForAsync(_tenantA, _tenantAAdmin);
        using var clientB = await ClientForAsync(_tenantB, _tenantBAdmin);

        var a = await UploadAndFinalizeAsync(clientA, "same-name.pdf", "application/pdf", RandomBytes(64));
        var b = await UploadAndFinalizeAsync(clientB, "same-name.pdf", "application/pdf", RandomBytes(64));

        var keyA = await StorageKeyOfAsync(_tenantA, a);
        var keyB = await StorageKeyOfAsync(_tenantB, b);

        keyA.ShouldStartWith($"tenants/{_tenantA}/");
        keyB.ShouldStartWith($"tenants/{_tenantB}/");
        keyA.ShouldNotBe(keyB);
    }

    #endregion

    #region The public space still works end to end

    [Fact]
    public async Task AvatarUpload_Should_Produce_A_Working_Anonymous_Url_Under_The_Tenants_PublicPrefix()
    {
        // The prefix move must not break the one thing `uploads/` exists for: an <img src> that
        // resolves without a signature. The bucket carries the deploy stacks' grant on `uploads/`
        // (AppWebApplicationFactory), so this is the real answer, not an inference from the shape.
        using var clientB = await ClientForAsync(_tenantB, _tenantBAdmin);

        var avatarUrl = await UploadAvatarAsync(clientB, "works.png");

        avatarUrl.ShouldContain($"/uploads/tenants/{_tenantB}/");
        avatarUrl.ShouldNotContain("X-Amz-Signature", Case.Insensitive);
        (await FetchAnonymouslyAsync(avatarUrl)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReplacingAnAvatar_Should_Delete_The_Object_It_Replaced()
    {
        // Before #78 this silently did nothing on path-style S3 (MinIO): the bucket segment of the
        // persisted URL survived into the key, so the delete addressed an object that never existed.
        using var clientB = await ClientForAsync(_tenantB, _tenantBAdmin);
        var first = await UploadAvatarAsync(clientB, "first.png");
        var firstKey = KeyFromPublicUrl(first);

        var second = await UploadAvatarAsync(clientB, "second.png");

        second.ShouldNotBe(first);
        await AsTenantAsync(_tenantB, async storage =>
        {
            (await storage.ExistsAsync(firstKey)).ShouldBeFalse("the replaced avatar's bytes must be gone");
            (await storage.ExistsAsync(KeyFromPublicUrl(second))).ShouldBeTrue();
        });
        (await FetchAnonymouslyAsync(first)).ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BrandAssetUpload_Should_Produce_A_Working_Anonymous_Url_Under_The_Tenants_PublicPrefix()
    {
        using var clientB = await ClientForAsync(_tenantB, _tenantBAdmin);

        var logoUrl = await UploadLogoAsync(clientB, "logo.png");

        logoUrl.ShouldContain($"/uploads/tenants/{_tenantB}/");
        (await FetchAnonymouslyAsync(logoUrl)).ShouldBe(HttpStatusCode.OK);

        // And tenant A cannot reach it, even holding the exact key.
        var key = KeyFromPublicUrl(logoUrl);
        await AsTenantAsync(_tenantA, async storage =>
            await Should.ThrowAsync<StorageKeyNotOwnedException>(() => storage.RemoveAsync(key)));
    }

    #endregion

    #region The Files round trip still works

    [Fact]
    public async Task Files_Should_Still_Upload_Finalize_Download_Trash_And_Purge()
    {
        // The whole flow across the new key shape, in one pass: the presigned PUT lands on the
        // composed key, finalize HEADs it, the download URL serves the same bytes, and the purge
        // job — which enters each tenant through ITenantScope — removes the object.
        using var clientB = await ClientForAsync(_tenantB, _tenantBAdmin);
        var bytes = RandomBytes(1024);
        var fileId = await UploadAndFinalizeAsync(clientB, "round-trip.pdf", "application/pdf", bytes);
        var key = await StorageKeyOfAsync(_tenantB, fileId);

        using var urlResponse = await clientB.GetAsync($"{FilesBasePath}/{fileId}/url");
        urlResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var download = await urlResponse.DeserializeAsync<PresignedDownloadResponse>();

        using var raw = new HttpClient();
        using var fetched = await raw.GetAsync(download.Url);
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fetched.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);

        using var trashed = await clientB.DeleteAsync($"{FilesBasePath}/{fileId}");
        trashed.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        await AsTenantAsync(_tenantB, async storage =>
        {
            // Soft delete keeps the bytes; the purge job is what removes them.
            (await storage.ExistsAsync(key)).ShouldBeTrue();
            await storage.RemoveAsync(key);
            (await storage.ExistsAsync(key)).ShouldBeFalse();
        });
    }

    #endregion

    #region Helpers

    private async Task<HttpClient> ClientForAsync(string tenantId, string adminEmail) =>
        await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);

    /// <summary>
    /// Runs <paramref name="work"/> with the storage service of <paramref name="tenantId"/> — the
    /// same entry a job uses, so the ambient tenant is installed before the scope is built.
    /// </summary>
    private Task AsTenantAsync(string tenantId, Func<IStorageService, Task> work) =>
        _factory.Services.GetRequiredService<ITenantScope>().RunAsync(
            tenantId,
            (services, _) => work(services.GetRequiredService<IStorageService>()),
            CancellationToken.None);

    /// <summary>The key a tenant's own row holds — read inside that tenant, never guessed.</summary>
    private Task<string> StorageKeyOfAsync(string tenantId, Guid fileAssetId) =>
        _factory.Services.GetRequiredService<ITenantScope>().RunAsync(
            tenantId,
            async (services, ct) =>
            {
                var db = services.GetRequiredService<FilesDbContext>();
                var asset = await db.FileAssets.AsNoTracking()
                    .FirstOrDefaultAsync(f => f.Id == fileAssetId, ct);
                asset.ShouldNotBeNull();
                return asset!.StorageKey;
            },
            CancellationToken.None);

    private static string KeyFromPublicUrl(string url)
    {
        // The durable URL is `{endpoint}/{bucket}/{key}` under path-style addressing; the key we
        // want starts at the public root.
        var path = new Uri(url).AbsolutePath;
        var start = path.IndexOf("/uploads/tenants/", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"'{url}' is not a durable public-space URL");
        return path[(start + 1)..];
    }

    private static async Task<HttpStatusCode> FetchAnonymouslyAsync(string url)
    {
        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync(new Uri(url));
        return response.StatusCode;
    }

    private static async Task<string> UploadAvatarAsync(HttpClient client, string fileName)
    {
        using var response = await client.PutAsJsonAsync($"{TestConstants.IdentityBasePath}/profile", new
        {
            firstName = "Ada",
            lastName = "Lovelace",
            // Not `deleteCurrentImage`: the validator refuses a request that both uploads and
            // deletes. Uploading is itself the replacement, and the handler drops what it replaced.
            image = new
            {
                fileName,
                contentType = "image/png",
                data = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Select(b => (int)b).ToArray(),
            },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var profile = await client.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        var dto = await profile.DeserializeAsync<UserDto>();
        dto.ImageUrl.ShouldNotBeNullOrWhiteSpace();
        return dto.ImageUrl!;
    }

    private static async Task<string> UploadLogoAsync(HttpClient client, string fileName)
    {
        using var response = await client.PutAsJsonAsync(ThemePath, ThemeWithLogo(fileName));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        using var theme = await client.GetAsync(ThemePath);
        theme.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await theme.Content.ReadFromJsonAsync<JsonElement>();
        var logoUrl = json.GetProperty("brandAssets").GetProperty("logoUrl").GetString();
        logoUrl.ShouldNotBeNullOrWhiteSpace();
        return logoUrl!;
    }

    private static object ThemeWithLogo(string fileName) => new
    {
        lightPalette = new
        {
            primary = "#2563EB",
            secondary = "#0F172A",
            tertiary = "#6366F1",
            background = "#F8FAFC",
            surface = "#FFFFFF",
            error = "#DC2626",
            warning = "#F59E0B",
            success = "#16A34A",
            info = "#0284C7",
        },
        darkPalette = new
        {
            primary = "#38BDF8",
            secondary = "#94A3B8",
            tertiary = "#818CF8",
            background = "#0B1220",
            surface = "#111827",
            error = "#F87171",
            warning = "#FBBF24",
            success = "#22C55E",
            info = "#38BDF8",
        },
        brandAssets = new
        {
            logo = new
            {
                fileName,
                contentType = "image/png",
                data = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Select(b => (int)b).ToArray(),
            },
        },
        typography = new
        {
            fontFamily = "Inter, sans-serif",
            headingFontFamily = "Inter, sans-serif",
            fontSizeBase = 14,
            lineHeightBase = 1.5,
        },
        layout = new
        {
            borderRadius = "4px",
            defaultElevation = 1,
        },
    };

    private static byte[] RandomBytes(int size)
    {
        byte[] bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static async Task<Guid> UploadAndFinalizeAsync(
        HttpClient client, string fileName, string contentType, byte[] bytes)
    {
        using var response = await client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName,
            contentType,
            sizeBytes = bytes.Length,
            visibility = 1,
            category = "Document",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var presigned = await response.DeserializeAsync<PresignedUploadResponse>();

        using var raw = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, presigned.UploadUrl)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(contentType) }
            }
        };
        using var putResponse = await raw.SendAsync(put);
        putResponse.EnsureSuccessStatusCode();

        using var finalize = await client.PostAsync($"{FilesBasePath}/{presigned.FileAssetId}/finalize", null);
        finalize.EnsureSuccessStatusCode();
        return presigned.FileAssetId;
    }

    #endregion
}
