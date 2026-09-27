using System.Text.RegularExpressions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Hangfire;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Jobs;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integration.Tests.Tests.Health;

/// <summary>
/// The Job monitor (Hangfire's dashboard) is a routed endpoint gated by
/// <see cref="SystemPermissions.Hangfire.View"/>, a root-only permission. API clients reach it with a
/// bearer token; a browser reaches it with the short-lived, route-scoped cookie the console asks for
/// (ADR-0009). Without <see cref="SystemPermissions.Hangfire.Manage"/> it is read-only, and its writes
/// are behind Hangfire's antiforgery check.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed partial class HangfireDashboardAuthTests
{
    private const string DashboardPath = "/jobs";
    private const string CookieName = "__Secure-job_monitor";
    private const string AntiforgeryCookieName = "job_monitor_antiforgery";
    private const string IssuePath = TestConstants.IdentityBasePath + "/operator/job-monitor-access";
    private const string ImpersonationStartPath = TestConstants.IdentityBasePath + "/impersonation/start";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public HangfireDashboardAuthTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    // ─── Bearer ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HangfireDashboard_Should_Return401_When_AnonymousRequest()
    {
        using var client = CreateBareClient();

        var response = await client.GetAsync(DashboardPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HangfireDashboard_Should_Return403_When_CallerLacksTheOperatorPermission()
    {
        var (email, password, _) = await CreateRootUserAsync("hf");
        using var client = await _auth.CreateAuthenticatedClientAsync(email, password);

        var response = await client.GetAsync(DashboardPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            $"Hangfire dashboard must require {SystemPermissions.Hangfire.View}.");
    }

    [Fact]
    public async Task HangfireDashboard_Should_ReturnOk_When_RootOperatorHasThePermission()
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        var response = await client.GetAsync(DashboardPath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ─── Issuing the cookie ─────────────────────────────────────────────

    [Fact]
    public async Task IssueAccess_Should_SetAHostOnlyStrictCookieScopedToTheJobMonitor()
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        var response = await client.PostAsync(IssuePath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal));
        var attributes = setCookie.Split(';', StringSplitOptions.TrimEntries).Skip(1)
            .Select(a => a.ToUpperInvariant()).ToList();

        attributes.ShouldContain("HTTPONLY");
        attributes.ShouldContain("SECURE");
        attributes.ShouldContain("SAMESITE=STRICT");
        attributes.ShouldContain("PATH=/JOBS");
        attributes.ShouldNotContain(a => a.StartsWith("DOMAIN=", StringComparison.Ordinal));

        var expires = DateTimeOffset.Parse(
            attributes.Single(a => a.StartsWith("EXPIRES=", StringComparison.Ordinal))["EXPIRES=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
        (expires - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15.5));

        var body = await response.Content.ReadFromJsonAsync<AccessResponse>();
        body!.Path.ShouldBe(DashboardPath);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(CookieValue(setCookie));
    }

    [Fact]
    public async Task IssueAccess_Should_Return403_When_CallerLacksTheOperatorPermission()
    {
        var (email, password, _) = await CreateRootUserAsync("hf-noperm");
        using var client = await _auth.CreateAuthenticatedClientAsync(email, password);

        var response = await client.PostAsync(IssuePath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task IssueAccess_Should_Return403_When_CallerIsActing()
    {
        // A root user who holds Hangfire.View, impersonated by the root admin: the acting token
        // carries the permission, so only the deny-when-acting rule can refuse it.
        var (_, _, userId) = await CreateRootUserAsync("hf-acted", SystemPermissions.Hangfire.View);
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var start = await rootClient.PostAsJsonAsync(ImpersonationStartPath, new
        {
            targetUserId = userId,
            targetTenantId = TestConstants.RootTenantId,
            reason = "job monitor acting test",
        });
        start.EnsureSuccessStatusCode();
        var acting = await start.Content.ReadFromJsonAsync<TokenBody>();
        using var actingClient = CreateBareClient();
        actingClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", acting!.AccessToken);

        var response = await actingClient.PostAsync(IssuePath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    // ─── Using the cookie ───────────────────────────────────────────────

    [Fact]
    public async Task JobMonitor_Should_ReturnOk_When_RequestCarriesTheCookie()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        var cookie = await IssueCookieAsync(admin);
        using var browser = CreateBareClient();

        var response = await SendWithCookiesAsync(browser, HttpMethod.Get, DashboardPath, cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task JobMonitor_Should_Return401_When_TheSessionHasLoggedOut()
    {
        var token = await _auth.GetRootAdminTokenAsync();
        using var operatorClient = CreateBareClient();
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var cookie = await IssueCookieAsync(operatorClient);

        var logout = await operatorClient.PostAsJsonAsync($"{TestConstants.RootAuthBasePath}/logout", new { });
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var browser = CreateBareClient();
        var response = await SendWithCookiesAsync(browser, HttpMethod.Get, DashboardPath, cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cookie_Should_BeIgnored_On_AnyOtherRoute()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        var cookie = await IssueCookieAsync(admin);
        using var browser = CreateBareClient();

        var response = await SendWithCookiesAsync(browser, HttpMethod.Get, $"{TestConstants.IdentityBasePath}/permissions", cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CookieToken_Should_BeRejected_As_ABearerToken()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        var cookie = await IssueCookieAsync(admin);
        using var client = CreateBareClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cookie);

        (await client.GetAsync(DashboardPath)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync($"{TestConstants.IdentityBasePath}/permissions")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccessToken_Should_BeRejected_In_TheCookie()
    {
        var token = await _auth.GetRootAdminTokenAsync();
        using var browser = CreateBareClient();

        var response = await SendWithCookiesAsync(browser, HttpMethod.Get, DashboardPath, token.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── Writes: Manage, and antiforgery ────────────────────────────────

    [Fact]
    public async Task ViewOnlyOperator_Should_NotBeAbleTo_DeleteOrRequeueAJob()
    {
        var (email, password, _) = await CreateRootUserAsync("hf-view", SystemPermissions.Hangfire.View);
        using var viewer = await _auth.CreateAuthenticatedClientAsync(email, password);
        var cookie = await IssueCookieAsync(viewer);
        var jobId = ScheduleProbeJob();

        using var browser = CreateBareClient();
        var (antiforgeryCookie, header, requestToken) = await ReadAntiforgeryAsync(browser, cookie);

        foreach (var action in new[] { "delete", "requeue" })
        {
            var response = await SendWithCookiesAsync(
                browser, HttpMethod.Post, $"{DashboardPath}/jobs/actions/{action}/{jobId}", cookie, antiforgeryCookie,
                (header, requestToken));

            // 401 is Hangfire's read-only answer; the antiforgery token was valid, so this is not a 403.
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"a View-only operator must not {action} a job");
        }

        JobState(jobId).ShouldBe("Scheduled");
    }

    [Fact]
    public async Task ManagingOperator_Should_DeleteAJob_WithTheAntiforgeryToken()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        var cookie = await IssueCookieAsync(admin);
        var jobId = ScheduleProbeJob();

        using var browser = CreateBareClient();
        var (antiforgeryCookie, header, requestToken) = await ReadAntiforgeryAsync(browser, cookie);

        var response = await SendWithCookiesAsync(
            browser, HttpMethod.Post, $"{DashboardPath}/jobs/actions/delete/{jobId}", cookie, antiforgeryCookie,
            (header, requestToken));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JobState(jobId).ShouldBe("Deleted");
    }

    [Fact]
    public async Task ManagingOperator_Should_BeRefused_A_WriteWithoutTheAntiforgeryToken()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        var cookie = await IssueCookieAsync(admin);
        var jobId = ScheduleProbeJob();
        using var browser = CreateBareClient();

        var response = await SendWithCookiesAsync(
            browser, HttpMethod.Post, $"{DashboardPath}/jobs/actions/delete/{jobId}", cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JobState(jobId).ShouldBe("Scheduled");
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// A client that manages no cookies itself. TestServer speaks http, and a cookie container would
    /// (rightly) never send a <c>Secure</c> cookie over it, so the tests set the Cookie header by hand.
    /// </summary>
    private HttpClient CreateBareClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    private static async Task<string> IssueCookieAsync(HttpClient operatorClient)
    {
        var response = await operatorClient.PostAsync(IssuePath, content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal));
        return CookieValue(setCookie);
    }

    private static string CookieValue(string setCookie) =>
        setCookie.Split(';', 2)[0][(setCookie.IndexOf('=', StringComparison.Ordinal) + 1)..];

    private static async Task<HttpResponseMessage> SendWithCookiesAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string jobMonitorCookie,
        string? antiforgeryCookie = null,
        (string Header, string Token)? antiforgery = null)
    {
        using var request = new HttpRequestMessage(method, path);
        var cookies = $"{CookieName}={jobMonitorCookie}";
        if (antiforgeryCookie is not null)
        {
            cookies += $"; {AntiforgeryCookieName}={antiforgeryCookie}";
        }

        request.Headers.Add("Cookie", cookies);
        if (antiforgery is { } af)
        {
            request.Headers.Add(af.Header, af.Token);
        }

        if (method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent([]);
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// What a browser gets from rendering any Job monitor page: the antiforgery cookie, and the
    /// header name plus request token Hangfire's script sends back on every write.
    /// </summary>
    private static async Task<(string Cookie, string Header, string Token)> ReadAntiforgeryAsync(
        HttpClient browser, string jobMonitorCookie)
    {
        var page = await SendWithCookiesAsync(browser, HttpMethod.Get, DashboardPath, jobMonitorCookie);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);

        var setCookie = page.Headers.GetValues("Set-Cookie")
            .Single(v => v.StartsWith(AntiforgeryCookieName + "=", StringComparison.Ordinal));
        var html = await page.Content.ReadAsStringAsync();

        var header = AntiforgeryHeaderPattern().Match(html);
        var token = AntiforgeryTokenPattern().Match(html);
        header.Success.ShouldBeTrue("the Job monitor page must carry the antiforgery header name");
        token.Success.ShouldBeTrue("the Job monitor page must carry the antiforgery request token");

        return (CookieValue(setCookie), WebUtility.HtmlDecode(header.Groups[1].Value), WebUtility.HtmlDecode(token.Groups[1].Value));
    }

    private string ScheduleProbeJob()
    {
        var jobs = _factory.Services.GetRequiredService<IBackgroundJobClient>();
        return jobs.Schedule<SystemProbeJob>(j => j.RunAsync(Guid.NewGuid(), CancellationToken.None), TimeSpan.FromDays(1));
    }

    private string? JobState(string jobId)
    {
        using var connection = _factory.Services.GetRequiredService<JobStorage>().GetConnection();
        return connection.GetJobData(jobId)?.State;
    }

    /// <summary>
    /// Seeds a confirmed, active root-tenant user whose only role grants exactly
    /// <paramref name="permissions"/> (none at all when empty). The Finbuckle tenant context is set
    /// INLINE because it is AsyncLocal.
    /// </summary>
    private async Task<(string Email, string Password, string UserId)> CreateRootUserAsync(
        string prefix, params string[] permissions)
    {
        const string password = TestConstants.DefaultPassword;
        var handle = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
        var email = $"{handle}@example.com";

        using var scope = _factory.Services.CreateScope();

        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            FirstName = "Job",
            LastName = "Monitor",
            Email = email,
            UserName = handle,
            EmailConfirmed = true,
            IsActive = true,
        };

        var result = await userManager.CreateAsync(user, password);
        result.Succeeded.ShouldBeTrue(
            $"Seeding active user failed: {string.Join(", ", result.Errors.Select(e => e.Description))}");

        if (permissions.Length > 0)
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
            var role = new AppRole($"jobmon-{handle}", "Job monitor test role");
            (await roleManager.CreateAsync(role)).Succeeded.ShouldBeTrue();

            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            foreach (var permission in permissions)
            {
                db.RoleClaims.Add(new AppRoleClaim
                {
                    RoleId = role.Id,
                    ClaimType = ClaimConstants.Permission,
                    ClaimValue = permission,
                    CreatedBy = "test",
                    CreatedOn = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync();
            (await userManager.AddToRoleAsync(user, role.Name!)).Succeeded.ShouldBeTrue();
        }

        return (email, password, user.Id);
    }

    [GeneratedRegex("<meta name=\"csrf-header\" content=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryHeaderPattern();

    [GeneratedRegex("<meta name=\"csrf-token\" content=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenPattern();

    private sealed record AccessResponse(string Path, DateTime ExpiresAt);

    private sealed record TokenBody(string AccessToken);
}
