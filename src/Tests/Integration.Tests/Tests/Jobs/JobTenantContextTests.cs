using Boilerplate.BuildingBlocks.Jobs.Services;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Jobs;

/// <summary>
/// ADR-0002, "Jobs and events", end to end through Hangfire's real client and perform pipeline
/// (<c>AppJobFilter</c> + <c>AppJobActivator</c>, wired by <c>UseHeroJobPipeline</c> in the test host):
///
/// <list type="bullet">
///   <item>a job enqueued from a background context — no HttpContext anywhere — inside
///     <c>ITenantScope.RunAsync(A)</c> runs under A and reads only A's rows;</item>
///   <item>an unmarked job enqueued with no ambient tenant is refused at the enqueue site;</item>
///   <item>an unmarked job that reaches the worker without a tenant parameter fails the job;</item>
///   <item>a <c>[SystemJob]</c> runs with no tenant at all.</item>
/// </list>
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class JobTenantContextTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);

    private readonly AppWebApplicationFactory _factory;
    private readonly TenantFixtures _tenants;

    public JobTenantContextTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _tenants = new TenantFixtures(factory);
    }

    [Fact]
    public async Task Job_Enqueued_From_A_Background_Scope_Should_Run_Under_The_Enqueuing_Tenant()
    {
        var (tenantA, adminA) = await _tenants.CreateProvisionedTenantAsync("joba");
        var (_, adminB) = await _tenants.CreateProvisionedTenantAsync("jobb");

        var marker = Guid.CreateVersion7();

        // No request, no HttpContext — exactly the situation the old filter gave up on.
        var tenantScope = _factory.Services.GetRequiredService<ITenantScope>();
        await tenantScope.RunAsync(tenantA, (services, _) =>
        {
            services.GetRequiredService<IJobService>()
                .Enqueue<TenantProbeJob>(job => job.RunAsync(marker, CancellationToken.None));
            return Task.CompletedTask;
        });

        var observation = await WaitForAsync(() => TenantProbeJob.Observations.GetValueOrDefault(marker));

        observation.TenantId.ShouldBe(tenantA, "the job must run under the tenant it was enqueued for");
        observation.VisibleUserEmails.ShouldContain(adminA);
        observation.VisibleUserEmails.ShouldNotContain(
            adminB, "a job running as tenant A must not see tenant B's rows");
    }

    [Fact]
    public async Task Enqueuing_An_Unmarked_Job_Without_A_Tenant_Should_Throw_At_The_Enqueue_Site()
    {
        await Task.CompletedTask;

        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobService>();
        var marker = Guid.CreateVersion7();

        // No ambient tenant: not inside a request, not inside an ITenantScope.
        var ex = Should.Throw<Exception>(() =>
            jobs.Enqueue<TenantProbeJob>(job => job.RunAsync(marker, CancellationToken.None)));

        FlattenMessages(ex).ShouldContain(
            "[SystemJob]",
            Case.Sensitive,
            "the failure must name the fix — either enqueue under a tenant or declare the job tenant-less");
        TenantProbeJob.Observations.ShouldNotContainKey(marker, "the job must never have been created");
    }

    [Fact]
    public async Task An_Unmarked_Job_Reaching_The_Worker_Without_A_Tenant_Should_Fail()
    {
        var marker = Guid.CreateVersion7();

        // Enqueue through a client with no filters at all, so AppJobFilter never stamps a tenant.
        // That is the shape of a job written by an older build, or by code that bypassed the client.
        var storage = _factory.Services.GetRequiredService<JobStorage>();
        var emptyFilters = new JobFilterCollection();
        var client = new BackgroundJobClient(
            storage,
            new BackgroundJobFactory(emptyFilters),
            new BackgroundJobStateChanger(emptyFilters));

        var jobId = client.Enqueue<TenantProbeJob>(job => job.RunAsync(marker, CancellationToken.None));

        var state = await WaitForAsync(() =>
        {
            var name = storage.GetMonitoringApi().JobDetails(jobId)?.History
                .Select(h => h.StateName)
                .FirstOrDefault(n => n is FailedState.StateName or DeletedState.StateName);
            return name is null ? null : new Box<string>(name);
        });

        state.Value.ShouldBe(FailedState.StateName);
        TenantProbeJob.Observations.ShouldNotContainKey(
            marker, "the job body must not run tenant-less against the default database");
    }

    [Fact]
    public async Task A_SystemJob_Should_Run_With_No_Tenant_Even_When_One_Is_Ambient()
    {
        var marker = Guid.CreateVersion7();

        var tenantScope = _factory.Services.GetRequiredService<ITenantScope>();
        await tenantScope.RunAsync(TestConstants.RootTenantId, (services, _) =>
        {
            services.GetRequiredService<IJobService>()
                .Enqueue<SystemProbeJob>(job => job.RunAsync(marker, CancellationToken.None));
            return Task.CompletedTask;
        });

        var observed = await WaitForAsync(() =>
            SystemProbeJob.Observations.TryGetValue(marker, out var tenantId) ? new Box<string?>(tenantId) : null);

        observed.Value.ShouldBeNull(
            "a [SystemJob] must not inherit the enqueuing tenant — tenant-less is the declaration");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private sealed record Box<T>(T Value);

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"The job did not reach the expected state within {JobTimeout}.");
    }

    private static string FlattenMessages(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
