using System.Net;
using System.Security.Claims;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Operators.ExchangeOperatorToken;
using Boilerplate.Modules.Identity.Features.v1.Operators.ExchangeOperatorToken;
using Boilerplate.Modules.Identity.Services;
using Finbuckle.MultiTenant.Abstractions;
using NSubstitute;

namespace Identity.Tests.Handlers;

/// <summary>
/// The rules that decide whether a token exchange happens at all. Everything past them (minting,
/// the grant row, the audit) is the shared <see cref="IImpersonationTokenIssuer"/>, which is
/// exercised end-to-end in Integration.Tests — here we prove the door only opens for a root
/// operator who is not already acting, and that each refusal has its own deliberate status.
/// </summary>
public sealed class ExchangeOperatorTokenCommandHandlerTests
{
    private const string TargetTenantId = "acme";
    private const string OperatorUserId = "11111111-1111-1111-1111-111111111111";
    private const string TenantAdminUserId = "22222222-2222-2222-2222-222222222222";

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IIdentityService _identityService = Substitute.For<IIdentityService>();
    private readonly IImpersonationTokenIssuer _tokenIssuer = Substitute.For<IImpersonationTokenIssuer>();
    private readonly IMultiTenantStore<AppTenantInfo> _tenantStore = Substitute.For<IMultiTenantStore<AppTenantInfo>>();
    private readonly ExchangeOperatorTokenCommandHandler _sut;

    public ExchangeOperatorTokenCommandHandlerTests()
    {
        _sut = new ExchangeOperatorTokenCommandHandler(_currentUser, _identityService, _tokenIssuer, _tenantStore);

        SignedInAs(MultitenancyConstants.Root.Id);
        ActiveTenant(TargetTenantId);
        TenantUserExists(TenantAdminUserId, isActive: true);
        _tokenIssuer.IssueAsync(Arg.Any<IssueActingTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new IssuedActingToken(
                AccessToken: "token",
                ExpiresAtUtc: DateTime.UtcNow.AddMinutes(15),
                Jti: "jti-1",
                GrantId: Guid.NewGuid(),
                TargetUserId: call.Arg<IssueActingTokenRequest>().TargetUserId,
                TargetUserName: "Tenant Admin")));
    }

    #region Happy path

    [Fact]
    public async Task Handle_Should_ActAsTenantAdmin_When_TargetUserIsOmitted()
    {
        // Arrange — the tenant record's AdminEmail is the fallback subject.
        var command = new ExchangeOperatorTokenCommand(TargetTenantId, TargetUserId: null, Reason: "support");

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.TargetUserId.ShouldBe(TenantAdminUserId);
        result.ActorUserId.ShouldBe(OperatorUserId);
        result.ActorTenantId.ShouldBe(MultitenancyConstants.Root.Id);
        await _identityService.Received(1).FindTenantUserAsync(
            TargetTenantId, null, "admin@acme.test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_PassRequestedDuration_To_TheSharedIssuer()
    {
        // Arrange — clamping is the issuer's job; the handler must not silently drop the request.
        var command = new ExchangeOperatorTokenCommand(TargetTenantId, null, "support", DurationMinutes: 45);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _tokenIssuer.Received(1).IssueAsync(
            Arg.Is<IssueActingTokenRequest>(r =>
                r.RequestedMinutes == 45
                && r.TargetTenantId == TargetTenantId
                && r.ActorTenantId == MultitenancyConstants.Root.Id
                && r.Reason == "support"),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Refusals

    [Fact]
    public async Task Handle_Should_Throw403_When_CallerIsNotInRootTenant()
    {
        // Arrange — the permission is root-only in the catalog, but a mis-seeded role must not
        // get through either: the handler checks the tenant itself.
        SignedInAs("some-tenant");
        var command = new ExchangeOperatorTokenCommand(TargetTenantId, null, "support");

        // Act + Assert
        await Should.ThrowAsync<ForbiddenException>(() => _sut.Handle(command, CancellationToken.None).AsTask());
        await _tokenIssuer.DidNotReceiveWithAnyArgs().IssueAsync(default!, default);
    }

    [Fact]
    public async Task Handle_Should_Throw401_When_CallerIsAnonymous()
    {
        // Arrange
        _currentUser.IsAuthenticated().Returns(false);
        var command = new ExchangeOperatorTokenCommand(TargetTenantId, null, "support");

        // Act + Assert
        await Should.ThrowAsync<UnauthorizedException>(() => _sut.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Should_Reject_When_CallerIsAlreadyActing()
    {
        // Arrange — an exchanged token carries act_sub; exchanging again would overwrite the only
        // record of who is really acting.
        SignedInAs(MultitenancyConstants.Root.Id, acting: true);
        var command = new ExchangeOperatorTokenCommand(TargetTenantId, null, "support");

        // Act
        var ex = await Should.ThrowAsync<CustomException>(
            () => _sut.Handle(command, CancellationToken.None).AsTask());

        // Assert
        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _tokenIssuer.DidNotReceiveWithAnyArgs().IssueAsync(default!, default);
    }

    [Fact]
    public async Task Handle_Should_Reject_When_TargetTenantIsRoot()
    {
        // Arrange
        var command = new ExchangeOperatorTokenCommand(MultitenancyConstants.Root.Id, null, "support");

        // Act
        var ex = await Should.ThrowAsync<CustomException>(
            () => _sut.Handle(command, CancellationToken.None).AsTask());

        // Assert
        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Handle_Should_Throw404_When_TargetTenantIsUnknown()
    {
        // Arrange
        _tenantStore.GetAsync("ghost").Returns(Task.FromResult<AppTenantInfo?>(null));
        var command = new ExchangeOperatorTokenCommand("ghost", null, "support");

        // Act + Assert
        await Should.ThrowAsync<NotFoundException>(() => _sut.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Should_Throw403_When_TargetTenantIsDeactivated()
    {
        // Arrange — the deactivated-tenant guard would reject every request made with the issued
        // token (its resolved tenant is the target), so we refuse instead of handing over a dud.
        ActiveTenant(TargetTenantId, isActive: false);
        var command = new ExchangeOperatorTokenCommand(TargetTenantId, null, "support");

        // Act + Assert
        await Should.ThrowAsync<ForbiddenException>(() => _sut.Handle(command, CancellationToken.None).AsTask());
        await _tokenIssuer.DidNotReceiveWithAnyArgs().IssueAsync(default!, default);
    }

    [Fact]
    public async Task Handle_Should_Throw404_When_TargetUserIsNotInTheTenant()
    {
        // Arrange
        _identityService.FindTenantUserAsync(
                TargetTenantId, "ghost-user", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantUserLookup?>(null));
        var command = new ExchangeOperatorTokenCommand(TargetTenantId, "ghost-user", "support");

        // Act + Assert
        await Should.ThrowAsync<NotFoundException>(() => _sut.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Should_Throw409_When_TargetUserIsDeactivated()
    {
        // Arrange — distinct from "missing": the identity exists, it just may not be borrowed.
        TenantUserExists(TenantAdminUserId, isActive: false);
        var command = new ExchangeOperatorTokenCommand(TargetTenantId, null, "support");

        // Act
        var ex = await Should.ThrowAsync<CustomException>(
            () => _sut.Handle(command, CancellationToken.None).AsTask());

        // Assert
        ex.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    #endregion

    // ─── helpers ────────────────────────────────────────────────────────

    private void SignedInAs(string tenantId, bool acting = false)
    {
        _currentUser.IsAuthenticated().Returns(true);
        _currentUser.GetUserId().Returns(Guid.Parse(OperatorUserId));
        _currentUser.GetTenant().Returns(tenantId);
        _currentUser.Name.Returns("Root Operator");
        _currentUser.GetUserClaims().Returns(acting
            ? [new Claim(ClaimConstants.ActorSubject, "someone-else")]
            : []);
    }

    private void ActiveTenant(string tenantId, bool isActive = true)
    {
        var tenant = new AppTenantInfo(tenantId, tenantId, "Acme")
        {
            AdminEmail = "admin@acme.test",
            IsActive = isActive,
        };
        _tenantStore.GetAsync(tenantId).Returns(Task.FromResult<AppTenantInfo?>(tenant));
    }

    private void TenantUserExists(string userId, bool isActive)
    {
        var lookup = new TenantUserLookup(userId, "tenant.admin", "admin@acme.test", isActive, EmailConfirmed: true);
        _identityService.FindTenantUserAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantUserLookup?>(lookup));
    }
}
