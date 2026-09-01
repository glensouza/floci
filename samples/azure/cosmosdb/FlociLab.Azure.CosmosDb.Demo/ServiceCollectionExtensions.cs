using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.Azure.CosmosDb;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The sample's entire public surface toward a host (docs/BLAZOR-PLAN.md §3, constraint 4):
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAzureCosmosDbDemo();
    /// </code>
    ///
    /// The page, the route and the nav entry all come with it — a host adds a ProjectReference and
    /// this line, and nothing else.
    /// </summary>
    public static IServiceCollection AddAzureCosmosDbDemo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton, not scoped: CosmosDbClientFactory caches a CosmosClient meant to live for the
        // process lifetime (see its remarks), and it is IDisposable so the container tears it down
        // on shutdown.
        services.TryAddSingleton<CosmosDbClientFactory>();

        // Registered by concrete type as well as by interface, because CosmosDbPage injects
        // CosmosDbDemo directly — a page that owns one service has no use for the whole catalog,
        // and the resolved-by-interface registrations below forward to the same instance rather
        // than building a second one.
        services.TryAddSingleton<CosmosDbDemo>();
        services.TryAddSingleton<CosmosDbDocumentDb>();

        // TryAddEnumerable, not TryAddSingleton: the catalog and the comparison pages resolve
        // IEnumerable<T>, so every sample has to be additive. TryAddSingleton would see another
        // sample's IServiceDemo already registered and silently drop this one; plain AddSingleton
        // would register Cosmos DB twice if a host called this method twice. TryAddEnumerable
        // de-duplicates on the implementation type, which is both.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, CosmosDbDemo>(sp => sp.GetRequiredService<CosmosDbDemo>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDocumentDbCapability, CosmosDbDocumentDb>(
                sp => sp.GetRequiredService<CosmosDbDocumentDb>()));

        return services;
    }
}
