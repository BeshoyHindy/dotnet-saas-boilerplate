using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Integration.Tests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace Integration.Tests.Tests.Authentication;

/// <summary>
/// ASVS V9.1.2: <c>ConfigureJwtBearerOptions</c> pins <c>ValidAlgorithms</c> to the one algorithm
/// <c>TokenService</c> actually signs with (<see cref="SecurityAlgorithms.HmacSha256"/>), rather than
/// trusting whatever <c>Microsoft.IdentityModel</c> accepts for a <see cref="SymmetricSecurityKey"/>
/// (HS256/384/512 all validate against the same key). A single configured signing key rules out the
/// asymmetric key-confusion V9.1.2 mainly targets, but a same-key algorithm swap is still worth
/// closing explicitly.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class JwtAlgorithmPinningTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public JwtAlgorithmPinningTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task AuthenticatedEndpoint_Should_Reject_TokenSignedWithDisallowedAlgorithm()
    {
        // Arrange — a genuine token, so issuer/audience/tenant claims are exactly what the server
        // expects; only the signing algorithm changes.
        var token = await _auth.GetRootAdminTokenAsync();
        var handler = new JwtSecurityTokenHandler();
        var original = handler.ReadJwtToken(token.AccessToken);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestConstants.JwtSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha512);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(original.Claims),
            NotBefore = original.ValidFrom,
            Expires = original.ValidTo,
            Issuer = TestConstants.JwtIssuer,
            Audience = TestConstants.JwtAudience,
            SigningCredentials = credentials
        };

        var forgedJwt = handler.CreateToken(descriptor);
        var forgedToken = handler.WriteToken(forgedJwt);

        // Act — the same key would validate this under HS512 if ValidAlgorithms allowed it.
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{TestConstants.IdentityBasePath}/sessions/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forgedToken);

        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
