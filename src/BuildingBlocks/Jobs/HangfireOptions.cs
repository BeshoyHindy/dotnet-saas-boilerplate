namespace Boilerplate.BuildingBlocks.Jobs;

public sealed class HangfireOptions
{
    /// <summary>
    /// Path the dashboard is mounted at. Access is gated by the platform's own authentication and
    /// the <c>Permissions.Hangfire.View</c> operator permission — there is no dashboard credential.
    /// </summary>
    public string Route { get; set; } = "/jobs";
}
