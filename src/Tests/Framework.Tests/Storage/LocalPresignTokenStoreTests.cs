using Boilerplate.BuildingBlocks.Storage.Local;

namespace Framework.Tests.Storage;

public sealed class LocalPresignTokenStoreTests
{
    private const string AcmeKey = "tenants/acme/probe/file.png";

    #region Happy Path

    [Fact]
    public void IssueThenConsume_Should_ReturnToken_When_NotExpired()
    {
        // Arrange
        var store = new LocalPresignTokenStore();

        // Act
        var token = store.Issue(AcmeKey, "image/png", 2048, TimeSpan.FromMinutes(5));
        var consumed = store.Consume(token, "acme");

        // Assert
        token.ShouldNotBeNullOrWhiteSpace();
        consumed.ShouldNotBeNull();
        consumed!.StorageKey.ShouldBe(AcmeKey);
        consumed.ContentType.ShouldBe("image/png");
        consumed.MaxBytes.ShouldBe(2048);
    }

    #endregion

    #region Tenant binding

    [Fact]
    public void Consume_Should_ReturnNull_When_AnotherTenantPresentsTheToken()
    {
        // A token is a bearer string; what stops it being a bearer capability for someone else's
        // object is that redeeming it re-checks the key's owner. Tenant B holding A's token gets
        // exactly what it gets for a token that never existed.
        var store = new LocalPresignTokenStore();
        var token = store.Issue(AcmeKey, "image/png", 2048, TimeSpan.FromMinutes(5));

        store.Consume(token, "globex").ShouldBeNull();
    }

    [Fact]
    public void Consume_Should_BurnTheToken_When_TheWrongTenantPresentsIt()
    {
        // The wrong-tenant attempt consumes it, so a leaked token cannot be probed and then used.
        var store = new LocalPresignTokenStore();
        var token = store.Issue(AcmeKey, "image/png", 2048, TimeSpan.FromMinutes(5));

        store.Consume(token, "globex").ShouldBeNull();
        store.Consume(token, "acme").ShouldBeNull();
    }

    [Fact]
    public void Consume_Should_ReturnNull_When_TheTokenCarriesAKeyNobodyOwns()
    {
        // Defence in depth: LocalStorageService only ever issues authorized keys, but a store that
        // trusted whatever it was handed would re-open the hole one layer down.
        var store = new LocalPresignTokenStore();
        var token = store.Issue("uploads/probe/legacy.png", "image/png", 1, TimeSpan.FromMinutes(5));

        store.Consume(token, "acme").ShouldBeNull();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Consume_Should_ReturnNull_When_TokenAlreadyConsumed()
    {
        // Arrange — tokens are one-shot.
        var store = new LocalPresignTokenStore();
        var token = store.Issue(AcmeKey, "text/plain", 1, TimeSpan.FromMinutes(5));

        // Act
        var first = store.Consume(token, "acme");
        var second = store.Consume(token, "acme");

        // Assert
        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public void Consume_Should_ReturnNull_When_TokenUnknown()
    {
        // Arrange
        var store = new LocalPresignTokenStore();

        // Act & Assert
        store.Consume("does-not-exist", "acme").ShouldBeNull();
    }

    [Fact]
    public void Consume_Should_ReturnNull_When_TokenExpired()
    {
        // Arrange — negative ttl makes the token expire immediately.
        var store = new LocalPresignTokenStore();
        var token = store.Issue(AcmeKey, "text/plain", 1, TimeSpan.FromMinutes(-5));

        // Act & Assert
        store.Consume(token, "acme").ShouldBeNull();
    }

    #endregion
}
