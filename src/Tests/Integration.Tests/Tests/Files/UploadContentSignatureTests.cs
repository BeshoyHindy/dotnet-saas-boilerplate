using System.Text;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Data;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Files;

/// <summary>
/// Finalize reads the stored object's first bytes and matches them against the declared type
/// (ASVS 5.0 V5.2.2, #125). The PUTs below send the <i>same</i> <c>Content-Type</c> the upload was
/// declared with, so the older header-vs-declaration check passes and only the bytes can refuse it.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class UploadContentSignatureTests
{
    private const string FilesBasePath = "/api/v1/files";
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public UploadContentSignatureTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    #region Finalize

    [Fact]
    public async Task Finalize_Should_Return400_And_RemoveTheObject_When_PngNamedFileIsHtml()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><script>alert(document.domain)</script></body></html>");
        var (id, storageKey) = await RequestAndPutAsync(client, "avatar.png", "image/png", html, "Image");

        (await ObjectExistsAsync(storageKey)).ShouldBeTrue("the HTML bytes reached storage");

        using var finalize = await client.PostAsync($"{FilesBasePath}/{id}/finalize", null);

        finalize.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ObjectExistsAsync(storageKey)).ShouldBeFalse("a refused upload must not stay in the store");
        using var second = await client.PostAsync($"{FilesBasePath}/{id}/finalize", null);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Finalize_Should_MakeTheFileAvailable_When_ItIsARealPng()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var (id, storageKey) = await RequestAndPutAsync(client, "avatar.png", "image/png", PngHeader, "Image");

        using var finalize = await client.PostAsync($"{FilesBasePath}/{id}/finalize", null);

        finalize.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await finalize.DeserializeAsync<FileAssetDto>()).Status.ShouldBe(FileAssetStatus.Available);
        (await ObjectExistsAsync(storageKey)).ShouldBeTrue();
    }

    [Fact]
    public async Task Finalize_Should_Return400_When_TextFileSniffsAsHtml()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        var (id, storageKey) = await RequestAndPutAsync(client, "notes.txt", "text/plain", html, "Document");

        using var finalize = await client.PostAsync($"{FilesBasePath}/{id}/finalize", null);

        finalize.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ObjectExistsAsync(storageKey)).ShouldBeFalse();
    }

    #endregion

    #region Request

    [Fact]
    public async Task UploadUrl_Should_Return400_When_DeclaredTypeIsNotOneTheExtensionAllows()
    {
        // Refused before a presigned PUT is signed for text/html.
        using var client = await _auth.CreateRootAdminClientAsync();

        using var response = await PostUploadUrlAsync(client, "notes.txt", "text/html", 64, "Document");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    #endregion

    // ─── helpers ─────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> PostUploadUrlAsync(
        HttpClient client, string fileName, string contentType, long sizeBytes, string category)
        => client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName,
            contentType,
            sizeBytes,
            visibility = 0,
            category,
        });

    private async Task<(Guid Id, string StorageKey)> RequestAndPutAsync(
        HttpClient client, string fileName, string contentType, byte[] bytes, string category)
    {
        using var response = await PostUploadUrlAsync(client, fileName, contentType, bytes.Length, category);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var presigned = await response.DeserializeAsync<PresignedUploadResponse>();

        using var raw = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, presigned.UploadUrl)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(contentType) }
            }
        };
        using var putResp = await raw.SendAsync(put);
        putResp.EnsureSuccessStatusCode();

        return (presigned.FileAssetId, await ReadStorageKeyAsync(presigned.FileAssetId));
    }

    private async Task<string> ReadStorageKeyAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        // Inline, not in an awaited helper: the tenant context is AsyncLocal.
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);

        var key = await scope.ServiceProvider.GetRequiredService<FilesDbContext>().FileAssets
            .IgnoreQueryFilters()
            .Where(f => f.Id == id)
            .Select(f => f.StorageKey)
            .FirstOrDefaultAsync();
        key.ShouldNotBeNull();
        return key;
    }

    private Task<bool> ObjectExistsAsync(string storageKey)
        => _factory.Services.GetRequiredService<ITenantScope>().RunAsync(
            TestConstants.RootTenantId,
            (services, ct) => services.GetRequiredService<IStorageService>().ExistsAsync(storageKey, ct),
            CancellationToken.None);
}
