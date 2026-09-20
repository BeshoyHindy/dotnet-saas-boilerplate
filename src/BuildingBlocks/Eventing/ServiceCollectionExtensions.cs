using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Inbox;
using Boilerplate.BuildingBlocks.Eventing.InMemory;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Eventing.Serialization;
using Boilerplate.BuildingBlocks.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Boilerplate.BuildingBlocks.Eventing;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds core eventing services (serializer, bus, options).
    /// </summary>
    public static IServiceCollection AddEventingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EventingOptions>().BindConfiguration(nameof(EventingOptions));

        services.AddSingleton<IEventSerializer, JsonEventSerializer>();

        // Tenant context for event dispatch (no-op default; multitenancy swaps in a Finbuckle scope)
        // so background publishers establish the tenant before tenant-filtered handler DbContexts build.
        services.TryAddSingleton<IEventTenantScope, NullEventTenantScope>();

        var options = configuration.GetSection(nameof(EventingOptions)).Get<EventingOptions>() ?? new EventingOptions();

        // The monolith's bus is in-process: handlers run in the same host, and cross-process
        // delivery is the outbox/inbox pair's job (ADR-0003 dropped the RabbitMQ provider).
        services.AddSingleton<IEventBus, InMemoryEventBus>();

        // Register outbox dispatcher hosted service if enabled
        if (options.UseHostedServiceDispatcher)
        {
            services.AddHostedService<OutboxDispatcherHostedService>();
        }

        // One framework-owned context owns the outbox/inbox tables, so each store has exactly
        // one registration. Registering them per module DbContext (the old
        // AddEventingForDbContext<T>) left .NET DI resolving whichever module registered last —
        // for the whole application, including Identity's working outbox (issue #1349).
        services.AddHeroDbContext<EventingDbContext>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDbInitializer, EventingDbInitializer>());
        services.TryAddScoped<IOutboxStore, EfCoreOutboxStore>();

        // Modules inject the publish-side contract from Eventing.Abstractions and stay off the
        // eventing runtime; it is the same instance as the store.
        services.TryAddScoped<IOutboxWriter>(sp => sp.GetRequiredService<IOutboxStore>());
        services.TryAddScoped<IInboxStore, EfCoreInboxStore>();
        services.TryAddScoped<OutboxDispatcher>();

        return services;
    }

    /// <summary>
    /// Registers integration event handlers from the specified assemblies.
    /// </summary>
    public static IServiceCollection AddIntegrationEventHandlers(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (assemblies is null || assemblies.Length == 0)
        {
            return services;
        }

        foreach (var assembly in assemblies)
        {
            var handlerTypes = assembly
                .GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .Select(t => new
                {
                    Type = t,
                    Interfaces = t.GetInterfaces()
                        .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>))
                        .ToArray()
                })
                .Where(x => x.Interfaces.Length > 0);

            foreach (var handler in handlerTypes)
            {
                foreach (var handlerInterface in handler.Interfaces)
                {
                    services.AddScoped(handlerInterface, handler.Type);
                }
            }
        }

        return services;
    }
}