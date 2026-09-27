using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity;
using Boilerplate.Modules.Identity.Services;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Identity.Tests.Services;

/// <summary>
/// <see cref="MailLinkOrigin"/> picks the console's origin only for the root tenant's own mail, and
/// only when that origin is actually configured — everyone else, and a scaffold that never set it,
/// keeps the dashboard's.
/// </summary>
public sealed class MailLinkOriginTests
{
    private const string DashboardOrigin = "https://dashboard.example.com";
    private const string ConsoleOrigin = "https://console.example.com";

    private readonly IOptions<OriginOptions> _originOptions;
    private readonly IOptions<MailLinkOriginOptions> _mailLinkOriginOptions;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _multiTenantContextAccessor;

    public MailLinkOriginTests()
    {
        _originOptions = Substitute.For<IOptions<OriginOptions>>();
        _originOptions.Value.Returns(new OriginOptions { OriginUrl = new Uri(DashboardOrigin) });

        _mailLinkOriginOptions = Substitute.For<IOptions<MailLinkOriginOptions>>();
        _mailLinkOriginOptions.Value.Returns(new MailLinkOriginOptions());

        _multiTenantContextAccessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();
    }

    private void SetAmbientTenant(string tenantId)
    {
        var context = Substitute.For<IMultiTenantContext<AppTenantInfo>>();
        context.TenantInfo.Returns(new AppTenantInfo(tenantId, tenantId, tenantId));
        _multiTenantContextAccessor.MultiTenantContext.Returns(context);
    }

    [Fact]
    public void Require_Should_ReturnConsoleOrigin_When_RecipientIsRootTenant_And_ConsoleOriginIsConfigured()
    {
        SetAmbientTenant(MultitenancyConstants.Root.Id);
        _mailLinkOriginOptions.Value.Returns(new MailLinkOriginOptions { ConsoleOriginUrl = new Uri(ConsoleOrigin) });

        var origin = MailLinkOrigin.Require(_originOptions, _mailLinkOriginOptions, _multiTenantContextAccessor);

        origin.ShouldStartWith(ConsoleOrigin);
    }

    [Fact]
    public void Require_Should_ReturnDashboardOrigin_When_RecipientIsRootTenant_And_ConsoleOriginIsNotConfigured()
    {
        SetAmbientTenant(MultitenancyConstants.Root.Id);
        // _mailLinkOriginOptions already returns an empty MailLinkOriginOptions (no ConsoleOriginUrl) —
        // the state a `--frontend false` scaffold, or an un-migrated deployment, is left in.

        var origin = MailLinkOrigin.Require(_originOptions, _mailLinkOriginOptions, _multiTenantContextAccessor);

        origin.ShouldStartWith(DashboardOrigin);
    }

    [Fact]
    public void Require_Should_ReturnDashboardOrigin_When_RecipientIsATenant_And_ConsoleOriginIsConfigured()
    {
        SetAmbientTenant("acme");
        _mailLinkOriginOptions.Value.Returns(new MailLinkOriginOptions { ConsoleOriginUrl = new Uri(ConsoleOrigin) });

        var origin = MailLinkOrigin.Require(_originOptions, _mailLinkOriginOptions, _multiTenantContextAccessor);

        origin.ShouldStartWith(DashboardOrigin);
    }

    [Fact]
    public void Require_Should_ReturnDashboardOrigin_When_RecipientIsATenant_And_ConsoleOriginIsNotConfigured()
    {
        SetAmbientTenant("acme");

        var origin = MailLinkOrigin.Require(_originOptions, _mailLinkOriginOptions, _multiTenantContextAccessor);

        origin.ShouldStartWith(DashboardOrigin);
    }

    [Fact]
    public void Require_Should_Throw_When_OriginUrlIsNotConfigured()
    {
        _originOptions.Value.Returns(new OriginOptions { OriginUrl = null });
        SetAmbientTenant(MultitenancyConstants.Root.Id);
        _mailLinkOriginOptions.Value.Returns(new MailLinkOriginOptions { ConsoleOriginUrl = new Uri(ConsoleOrigin) });

        Should.Throw<InvalidOperationException>(() =>
            MailLinkOrigin.Require(_originOptions, _mailLinkOriginOptions, _multiTenantContextAccessor));
    }
}
