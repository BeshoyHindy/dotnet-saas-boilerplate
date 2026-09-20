using Boilerplate.BuildingBlocks.Shared.Security;

namespace Framework.Tests.Security;

/// <summary>
/// The one list that decides what never reaches a request fingerprint. It is worth a table rather
/// than a paragraph: a false negative here is a secret's digest sitting in Redis for the entry's
/// whole TTL, and the two match rules — substring anywhere, short word only at a trailing boundary —
/// are exactly the kind of thing that drifts from its own documentation.
/// </summary>
public sealed class SensitiveFieldNamesTests
{
    [Theory]
    // Substrings: these mean "secret" wherever they appear, in any casing, at any position.
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("confirmPassword")]
    [InlineData("currentPassword")]
    [InlineData("newPassword")]
    [InlineData("passwordHash")]
    [InlineData("passphrase")]
    [InlineData("clientSecret")]
    [InlineData("secret")]
    [InlineData("refreshToken")]
    [InlineData("access_token")]
    [InlineData("credential")]
    [InlineData("awsCredentials")]
    // A connection string is a credential. No shipped command binds one, and a consumer's does.
    [InlineData("connectionString")]
    [InlineData("ConnectionString")]
    [InlineData("tenantConnectionStringOverride")]
    // Trailing words: whole name, after a case change, or after a separator.
    [InlineData("otp")]
    [InlineData("code")]
    [InlineData("key")]
    [InlineData("pin")]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("resetCode")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("api-key")]
    [InlineData("api.key")]
    [InlineData("webhookSignature")]
    [InlineData("userPin")]
    // Documented false positive, accepted on purpose: the cost is a replay where a 422 was possible.
    [InlineData("countryCode")]
    public void Names_That_Read_As_A_Secret_Should_Be_Sensitive(string name) =>
        SensitiveFieldNames.IsSensitive(name).ShouldBeTrue($"'{name}' should read as a secret.");

    [Theory]
    // The reason the short words are not substrings: these all contain one and mean nothing secret.
    [InlineData("encoded")]
    [InlineData("keyboard")]
    [InlineData("monkey")]
    [InlineData("keyword")]
    [InlineData("nonceremony")]
    [InlineData("pinned")]
    [InlineData("codec")]
    // A "connection" that is not a connection string.
    [InlineData("connectionId")]
    [InlineData("connectionCount")]
    // Ordinary payload fields, which must keep differing a retry into a 422.
    [InlineData("firstName")]
    [InlineData("email")]
    [InlineData("userName")]
    [InlineData("fileName")]
    [InlineData("contentType")]
    [InlineData("sizeBytes")]
    [InlineData("ownerType")]
    [InlineData("category")]
    [InlineData("adminEmail")]
    public void Ordinary_Names_Should_Not_Be_Sensitive(string name) =>
        SensitiveFieldNames.IsSensitive(name).ShouldBeFalse($"'{name}' is not a secret.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_Names_Should_Not_Be_Sensitive(string? name) =>
        SensitiveFieldNames.IsSensitive(name).ShouldBeFalse("there is no name to read as anything.");

    [Fact]
    public void Matching_Should_Be_Case_Insensitive_For_Both_Rules()
    {
        // Substring and trailing-word alike: the same name arrives camelCase from JSON and PascalCase
        // from reflection, and the two must not disagree.
        SensitiveFieldNames.IsSensitive("REFRESHTOKEN").ShouldBeTrue();
        SensitiveFieldNames.IsSensitive("ApiKEY").ShouldBeTrue();
        SensitiveFieldNames.IsSensitive("API_KEY").ShouldBeTrue();
    }

    [Fact]
    public void A_Short_Word_Should_Match_Only_At_A_Trailing_Boundary()
    {
        // "key" as the last word, not "key" anywhere: the distinction the whole two-list design exists
        // for, and the one a future edit is most likely to flatten.
        SensitiveFieldNames.IsSensitive("key").ShouldBeTrue("a whole name is a boundary.");
        SensitiveFieldNames.IsSensitive("signingKey").ShouldBeTrue("a case change is a boundary.");
        SensitiveFieldNames.IsSensitive("signing_key").ShouldBeTrue("a separator is a boundary.");
        SensitiveFieldNames.IsSensitive("keyName").ShouldBeFalse("leading, not trailing.");
        SensitiveFieldNames.IsSensitive("turkey").ShouldBeFalse("no boundary before it.");
    }
}
