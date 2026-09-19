using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Contracts.v1.CreateTenant;
using Boilerplate.Modules.Multitenancy.Features.v1.CreateTenant;
using NSubstitute;

namespace Multitenancy.Tests.Validators;

/// <summary>
/// The tenant Id is not just a primary key: it is interpolated into the anonymous auth routes, into
/// the refresh cookie's <c>Path</c>, and it is the prefix of every refresh token. It is also
/// immutable, so this validator is the only chance to keep it to a slug.
/// </summary>
public sealed class CreateTenantCommandValidatorTests
{
    private readonly ITenantService _tenantService = Substitute.For<ITenantService>();
    private readonly CreateTenantCommandValidator _sut;

    public CreateTenantCommandValidatorTests()
    {
        _tenantService.ExistsWithIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _tenantService.ExistsWithNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var connectionStrings = Substitute.For<IConnectionStringValidator>();
        connectionStrings.TryValidate(Arg.Any<string>()).Returns(true);

        _sut = new CreateTenantCommandValidator(_tenantService, connectionStrings, TimeProvider.System);
    }

    private static CreateTenantCommand CommandWithId(string id) => new(
        Id: id,
        Name: $"Tenant {id}",
        ConnectionString: null,
        AdminEmail: "admin@tenant.com",
        AdminPassword: "123Pa$$word!",
        Issuer: null,
        ValidUpto: null);

    [Theory]
    [InlineData("root")]           // the seeded tenant must stay valid
    [InlineData("ab")]             // shortest accepted
    [InlineData("acme-eu-west-1")]
    [InlineData("a1")]
    [InlineData("0contoso")]
    public async Task Validate_Should_Accept_LowercaseSlugs(string id)
    {
        var result = await _sut.ValidateAsync(CommandWithId(id));

        result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Theory]
    [InlineData("a")]                       // too short
    [InlineData("-leading-hyphen")]
    [InlineData("Acme")]                    // uppercase
    [InlineData("acme corp")]               // space
    [InlineData("acme.corp")]               // dot — would break the refresh token's tenant prefix
    [InlineData("acme/../root")]            // path traversal into the cookie Path
    [InlineData("acme;Path=/")]             // cookie attribute injection
    [InlineData("acme\nSet-Cookie: x=y")]   // header splitting
    [InlineData("tenant_1")]                // underscore
    public async Task Validate_Should_Reject_AnythingThatIsNotASlug(string id)
    {
        var result = await _sut.ValidateAsync(CommandWithId(id));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(CreateTenantCommand.Id));
    }

    [Fact]
    public async Task Validate_Should_Reject_IdsLongerThan63Characters()
    {
        // 63 is the DNS label limit; a tenant Id that cannot be a subdomain is a trap for later.
        (await _sut.ValidateAsync(CommandWithId(new string('a', 63)))).IsValid.ShouldBeTrue();
        (await _sut.ValidateAsync(CommandWithId(new string('a', 64)))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Validate_Should_Reject_DuplicateId_AfterTheShapeCheckPasses()
    {
        _tenantService.ExistsWithIdAsync("acme", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.ValidateAsync(CommandWithId("acme"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage.Contains("already exists", StringComparison.Ordinal));
    }
}
