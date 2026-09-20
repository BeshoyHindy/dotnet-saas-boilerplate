using Hangfire.Common;
using System.Reflection;

namespace Boilerplate.BuildingBlocks.Jobs;

/// <summary>
/// Reads the <see cref="SystemJobAttribute"/> marker off a Hangfire <see cref="Job"/>, and names a
/// job for error messages. Shared by the client filter (enqueue time) and the activator (run time)
/// so both sides agree on what "system job" means.
/// </summary>
internal static class SystemJobs
{
    public static bool IsSystemJob(Job? job)
    {
        if (job is null)
        {
            return false;
        }

        return job.Method.GetCustomAttribute<SystemJobAttribute>(inherit: true) is not null
            || (job.Method.ReflectedType ?? job.Method.DeclaringType)
                ?.GetCustomAttribute<SystemJobAttribute>(inherit: true) is not null
            || job.Type?.GetCustomAttribute<SystemJobAttribute>(inherit: true) is not null;
    }

    public static string Describe(Job? job) =>
        job is null
            ? "<unknown job>"
            : $"{job.Method.ReflectedType?.FullName ?? job.Type?.FullName}.{job.Method.Name}";
}
