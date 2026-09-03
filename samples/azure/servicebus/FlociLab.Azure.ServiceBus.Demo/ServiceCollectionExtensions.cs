using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.Azure.ServiceBus;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The sample's entire public surface toward a host (docs/BLAZOR-PLAN.md §3, constraint 4):
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAzureServiceBusDemo();
    /// </code>
    ///
    /// The page, the route and the nav entry all come with it — a host adds a ProjectReference and
    /// this line, and nothing else.
    /// </summary>
    public static IServiceCollection AddAzureServiceBusDemo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ServiceBusClientFactory>();

        // Registered by concrete type as well as by interface, because ServiceBusPage injects
        // ServiceBusDemo directly — a page that owns one service has no use for the whole catalog,
        // and the resolved-by-interface registrations below forward to the same instance rather
        // than building a second one.
        services.TryAddSingleton<ServiceBusDemo>();
        services.TryAddSingleton<ServiceBusQueue>();

        // TryAddEnumerable, not TryAddSingleton: the catalog and the comparison pages resolve
        // IEnumerable<T>, so every sample has to be additive. TryAddSingleton would see another
        // sample's IServiceDemo already registered and silently drop this one; plain AddSingleton
        // would register Service Bus twice if a host called this method twice. TryAddEnumerable
        // de-duplicates on the implementation type, which is both.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, ServiceBusDemo>(sp => sp.GetRequiredService<ServiceBusDemo>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IQueueCapability, ServiceBusQueue>(
                sp => sp.GetRequiredService<ServiceBusQueue>()));

        return services;
    }
}
