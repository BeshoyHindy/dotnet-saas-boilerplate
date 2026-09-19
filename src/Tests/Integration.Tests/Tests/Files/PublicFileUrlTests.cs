using System.Security.Cryptography;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Files;

/// <summary>
/// Locks down how a <c>Visibility=Public</c> Files asset is served (issue #52).
///
/// Files objects live under <c>tenants/{tenantId}/…</c>, a key space that public and private files
/// share and that the deploy stacks deliberately never grant anonymous read on (only <c>uploads/</c>,
/// where avatars and tenant theme assets live, is anonymously readable). So <c>publicUrl</c> cannot
/// be an unsigned bucket URL — it is a <b>short-lived presigned GET</b>, minted at read time and
/// never persisted.
///
/// Revocation semantics, stated precisely:
/// <list type="bullet">
///   <item>Flipping Public → Private is <b>immediate for the API</b>: every read endpoint stops
///   emitting a <c>publicUrl</c> at once.</item>
///   <item>A URL <b>already handed out</b> stays valid until its presign expiry (bounded by
///   <c>Files:PublicUrlTtlMinutes</c>, short by default) — that is inherent to presigning and is why
///   the TTL is kept short.</item>
/// </list>
/// These tests run against the real MinIO testcontainer wired up by <see cref="AppWebApplicationFactory"/>.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class PublicFileUrlTests
{
    private const string FilesBasePath = "/api/v1/files";
    private readonly AuthHelper _auth;

    public PublicFileUrlTests(AppWebApplicationFactory factory)
    {
        _auth = new AuthHelper(factory);
    }

    #region Happy Path — a public file is fetchable through the URL the API returns

    [Fact]
    public async Task GetMetadata_Should_Return_A_Fetchable_PublicUrl_When_File_Is_Public()
    {
        // Arrange — a Public file with known bytes in real S3-compatible storage.
        using var client = await _auth.CreateRootAdminClientAsync();
        var bytes = RandomBytes(1024);
        var id = await UploadAndFinalizeAsync(client, "public-asset.pdf", "application/pdf", bytes, visibility: 0);

        // Act — read the metadata the SPA uses to paint the asset, then fetch that URL with no auth
        // at all (an <img src> carries no bearer token).
        var dto = await GetMetadataAsync(client, id);
        dto.PublicUrl.ShouldNotBeNullOrWhiteSpace();

        using var anonymous = new HttpClient();
        using var fetched = await anonymous.GetAsync(new Uri(dto.PublicUrl!));

        // Assert — 200 with the exact bytes (before the fix this 403'd: unsigned tenants/ URL).
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fetched.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
    }

    [Fact]
    public async Task GetMetadata_Should_Return_A_ShortLived_Presigned_PublicUrl_When_File_Is_Public()
    {
        // Arrange
        using var client = await _auth.CreateRootAdminClientAsync();
        var id = await UploadAndFinalizeAsync(client, "signed.pdf", "application/pdf", RandomBytes(128), visibility: 0);

        // Act
        var dto = await GetMetadataAsync(client, id);

        // Assert — a presigned GET, not the bucket's unsigned object URL, and short-lived.
        dto.PublicUrl.ShouldNotBeNull();
        dto.PublicUrl!.ShouldContain("X-Amz-Signature");
        var expires = ExpiresSeconds(dto.PublicUrl!);
        expires.ShouldBeGreaterThan(0);
        expires.ShouldBeLessThanOrEqualTo(15 * 60, "public reads must stay short-lived — revocation is bounded by this TTL");
    }

    [Fact]
    public async Task ChangeVisibility_Should_Return_A_Fetchable_PublicUrl_When_Flipped_To_Public()
    {
        // Arrange — the PATCH response itself carries the URL the SPA renders immediately.
        using var client = await _auth.CreateRootAdminClientAsync();
        var bytes = RandomBytes(256);
        var id = await UploadAndFinalizeAsync(client, "flip-to-public.pdf", "application/pdf", bytes, visibility: 1);

        // Act
        using var flip = await client.PatchAsJsonAsync($"{FilesBasePath}/{id}/visibility", new { visibility = 0 });
        flip.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await flip.DeserializeAsync<FileAssetDto>();

        // Assert
        dto.PublicUrl.ShouldNotBeNullOrWhiteSpace();
        using var anonymous = new HttpClient();
        using var fetched = await anonymous.GetAsync(new Uri(dto.PublicUrl!));
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fetched.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
    }

    [Fact]
    public async Task ListShared_Should_Return_Fetchable_PublicUrls()
    {
        // Arrange
        using var client = await _auth.CreateRootAdminClientAsync();
        var bytes = RandomBytes(256);
        var id = await UploadAndFinalizeAsync(client, "listed-public.pdf", "application/pdf", bytes, visibility: 0);

        // Act
        using var response = await client.GetAsync($"{FilesBasePath}/shared?page=1&pageSize=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = await response.DeserializeAsync<IReadOnlyList<FileAssetDto>>();
        var row = list.Single(f => f.Id == id);

        // Assert — list rows seed the same fetchable URL (the preview dialog paints straight from them).
        row.PublicUrl.ShouldNotBeNullOrWhiteSpace();
        using var anonymous = new HttpClient();
        using var fetched = await anonymous.GetAsync(new Uri(row.PublicUrl!));
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fetched.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
    }

    #endregion

    #region The tenants/ key space is never anonymously readable

    [Fact]
    public async Task UnsignedObjectUrl_Should_Be_Rejected_For_A_Public_File_In_The_Tenants_KeySpace()
    {
        // Arrange — take the presigned URL the API returned and strip the signature query.
        using var client = await _auth.CreateRootAdminClientAsync();
        var id = await UploadAndFinalizeAsync(client, "no-anon-grant.pdf", "application/pdf", RandomBytes(128), visibility: 0);
        var dto = await GetMetadataAsync(client, id);

        var unsigned = new Uri(new Uri(dto.PublicUrl!).GetLeftPart(UriPartial.Path));
        unsigned.AbsolutePath.ShouldContain("/tenants/");

        // Act — what an unsigned PublicBaseUrl link would have been.
        using var anonymous = new HttpClient();
        using var fetched = await anonymous.GetAsync(unsigned);

        // Assert — the bucket grants anonymous read on uploads/ only; tenants/ stays closed, so the
        // signature (not the bucket policy) is what carries the access.
        fetched.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMetadata_Should_Return_No_PublicUrl_When_File_Is_Private()
    {
        // Arrange
        using var client = await _auth.CreateRootAdminClientAsync();
        var id = await UploadAndFinalizeAsync(client, "secret.pdf", "application/pdf", RandomBytes(128), visibility: 1);

        // Act
        var dto = await GetMetadataAsync(client, id);

        // Assert — private files are only reachable through the auth-gated /url endpoint.
        dto.Visibility.ShouldBe(Visibility.Private);
        dto.PublicUrl.ShouldBeNull();
    }

    [Fact]
    public async Task ListMy_Should_Return_No_PublicUrl_For_A_Private_File()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var id = await UploadAndFinalizeAsync(client, "secret-listed.pdf", "application/pdf", RandomBytes(128), visibility: 1);

        using var response = await client.GetAsync($"{FilesBasePath}/mine?page=1&pageSize=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = await response.DeserializeAsync<IReadOnlyList<FileAssetDto>>();

        list.First(f => f.Id == id).PublicUrl.ShouldBeNull();
    }

    #endregion

    #region Revocation

    [Fact]
    public async Task ChangeVisibility_To_Private_Should_Stop_The_Api_Issuing_Urls_While_An_Issued_Url_Lives_To_Expiry()
    {
        // Arrange — a Public file whose URL has already been handed to a browser.
        using var client = await _auth.CreateRootAdminClientAsync();
        var bytes = RandomBytes(256);
        var id = await UploadAndFinalizeAsync(client, "revoke-me.pdf", "application/pdf", bytes, visibility: 0);
        var issued = new Uri((await GetMetadataAsync(client, id)).PublicUrl!);

        using var anonymous = new HttpClient();
        using (var before = await anonymous.GetAsync(issued))
        {
            before.StatusCode.ShouldBe(HttpStatusCode.OK, "the public URL must work before revocation");
        }

        // Act — the uploader un-shares the file.
        using var flip = await client.PatchAsJsonAsync($"{FilesBasePath}/{id}/visibility", new { visibility = 1 });
        flip.StatusCode.ShouldBe(HttpStatusCode.OK);
        var flipped = await flip.DeserializeAsync<FileAssetDto>();

        // Assert 1 — IMMEDIATE: the API stops issuing URLs the moment visibility flips.
        flipped.Visibility.ShouldBe(Visibility.Private);
        flipped.PublicUrl.ShouldBeNull();
        (await GetMetadataAsync(client, id)).PublicUrl.ShouldBeNull();

        using var sharedList = await client.GetAsync($"{FilesBasePath}/shared?page=1&pageSize=100");
        var shared = await sharedList.DeserializeAsync<IReadOnlyList<FileAssetDto>>();
        shared.ShouldNotContain(f => f.Id == id);

        // Assert 2 — NOT retroactive: the signature already in the browser's hands stays valid until
        // it expires (bounded by Files:PublicUrlTtlMinutes). Documented, deliberate, and the reason
        // the TTL is minutes rather than hours. Callers needing instant hard revocation must delete
        // the object (DELETE /files/{id} + purge), not just flip the bit.
        using var after = await anonymous.GetAsync(issued);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await after.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
    }

    #endregion

    #region Helpers

    private static long ExpiresSeconds(string url)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("X-Amz-Expires", StringComparison.OrdinalIgnoreCase))
            {
                return long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    private static async Task<FileAssetDto> GetMetadataAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"{FilesBasePath}/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.DeserializeAsync<FileAssetDto>();
    }

    private static byte[] RandomBytes(int size)
    {
        byte[] bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static async Task<Guid> UploadAndFinalizeAsync(
        HttpClient client, string fileName, string contentType, byte[] bytes, int visibility)
    {
        using var response = await client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName,
            contentType,
            sizeBytes = bytes.Length,
            visibility,
            category = "Document",
        });
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

        using var finalize = await client.PostAsync($"{FilesBasePath}/{presigned.FileAssetId}/finalize", null);
        finalize.EnsureSuccessStatusCode();
        return presigned.FileAssetId;
    }

    #endregion
}
