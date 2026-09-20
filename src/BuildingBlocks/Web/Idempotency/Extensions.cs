using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.BuildingBlocks.Web.Idempotency;

public static class Extensions
{
    /// <summary>
    /// Registers idempotency options for use by IdempotencyEndpointFilter.
    /// Apply to specific endpoints via .WithIdempotency() extension.
    /// </summary>
    public static IServiceCollection AddHeroIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IdempotencyOptions>()
            .BindConfiguration(nameof(IdempotencyOptions))
            // A zero or negative wait is not "no lock", it is a lock that never serialises anything:
            // SemaphoreSlim treats a zero timeout as a poll and a negative one as "wait forever",
            // which is precisely the unbounded queue the bound exists to remove. Fail the boot instead.
            .Validate(
                o => o.LockWaitTimeout > TimeSpan.Zero,
                $"{nameof(IdempotencyOptions)}.{nameof(IdempotencyOptions.LockWaitTimeout)} must be greater than zero.")
            .ValidateOnStart();

        return services;
    }
}
