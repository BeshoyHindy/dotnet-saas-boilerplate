using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Domain;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// The registration email must link to a page a human can use. The mailed URL points at the client's
/// <c>/confirm-email</c> route (carrying userId, code and tenant), not at the raw API route — landing
/// on a JSON body was the defect. The page then calls the tenant-routed API endpoint, which this test
/// replays with the very parameters the mail carried.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class ConfirmationEmailLinkTests
{
    /// <summary>Matches <c>OriginOptions:OriginUrl</c> in <see cref="AppWebApplicationFactory"/>.</summary>
    private const string ConfiguredOrigin = "http://localhost";

    private readonly AppWebApplicationFactory _factory;

    public ConfirmationEmailLinkTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    #region Happy Path

    [Fact]
    public async Task Registration_Should_MailAClientPageLink_That_ConfirmsAgainstTheTenantRoutedEndpoint()
    {
        // Arrange
        var mail = (NoOpMailService)_factory.Services.GetRequiredService<IMailService>();
        mail.Clear();

        using var client = _factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"confirm-link-{unique}@example.com";

        // Act — self-register; the confirmation mail is enqueued on the "email" Hangfire queue.
        var response = await client.PostAsJsonAsync($"{TestConstants.RootAuthBasePath}/register", new
        {
            firstName = "Confirm",
            lastName = "Link",
            email,
            userName = $"confirmlink-{unique}",
            password = TestConstants.DefaultPassword,
            confirmPassword = TestConstants.DefaultPassword,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var registered = await response.DeserializeAsync<RegisterResult>();

        var confirmationMail = await WaitForMailAsync(mail, email);
        var link = ExtractConfirmationLink(confirmationMail);

        // Assert — the link is the SPA route, with the tenant in the query (the page reads it from there).
        link.GetLeftPart(UriPartial.Path).ShouldBe($"{ConfiguredOrigin}/confirm-email");
        link.OriginalString.ShouldNotContain("/api/", Case.Insensitive);

        var query = QueryHelpers.ParseQuery(link.Query);
        query["userId"].ToString().ShouldBe(registered.UserId);
        query["tenant"].ToString().ShouldBe(TestConstants.RootTenantId);
        var code = query["code"].ToString();
        code.ShouldNotBeNullOrWhiteSpace();

        // Act — replay what the page does with those parameters: call the tenant-routed API endpoint.
        var tenant = query["tenant"].ToString();
        var confirm = await client.GetAsync(
            $"{TestConstants.AuthBasePath(tenant)}/confirm-email" +
            $"?userId={Uri.EscapeDataString(query["userId"].ToString())}&code={Uri.EscapeDataString(code)}");

        // Assert
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var tenantInfo = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenantInfo);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var confirmed = await userManager.FindByIdAsync(registered.UserId);
        confirmed.ShouldNotBeNull();
        confirmed.EmailConfirmed.ShouldBeTrue();
    }

    #endregion

    #region Helpers

    private static async Task<MailRequest> WaitForMailAsync(NoOpMailService mail, string to)
    {
        // The mail is dispatched by the in-memory Hangfire server, so it lands a beat after the response.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var match = mail.Sent.FirstOrDefault(m => m.To.Contains(to));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(200);
        }

        throw new InvalidOperationException(
            string.Format(CultureInfo.InvariantCulture, "No confirmation mail reached {0} within 30s.", to));
    }

    private static Uri ExtractConfirmationLink(MailRequest request)
    {
        // The body embeds the URL HTML-encoded ("&amp;"); decode before parsing.
        var body = System.Net.WebUtility.HtmlDecode(request.Body ?? string.Empty);
        var match = Regex.Match(body, @"https?://[^\s""'<>]+/confirm-email\?[^\s""'<>]+", RegexOptions.None, TimeSpan.FromSeconds(5));
        match.Success.ShouldBeTrue($"No confirm-email link found in the mail body:{Environment.NewLine}{body}");
        return new Uri(match.Value);
    }

    #endregion
}
