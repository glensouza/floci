using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.Aws.S3;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The sample's entire public surface toward a host (docs/BLAZOR-PLAN.md §3, constraint 4):
    ///
    /// <code>
    /// builder.Services
    ///     .AddFlociCore(builder.Configuration)
    ///     .AddAwsS3Demo();
    /// </code>
    ///
    /// The page, the route and the nav entry all come with it — a host adds a ProjectReference and
    /// this line, and nothing else.
    /// </summary>
    public static IServiceCollection AddAwsS3Demo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<S3ClientFactory>();

        // Registered by concrete type as well as by interface, because S3Page injects S3Demo
        // directly — a page that owns one service has no use for the whole catalog, and the
        // resolved-by-interface registrations below forward to the same instance rather than
        // building a second one.
        services.TryAddSingleton<S3Demo>();
        services.TryAddSingleton<S3ObjectStore>();

        // TryAddEnumerable, not TryAddSingleton: the catalog and the comparison pages resolve
        // IEnumerable<T>, so every sample has to be additive. TryAddSingleton would see another
        // sample's IServiceDemo already registered and silently drop this one; plain AddSingleton
        // would register S3 twice if a host called this method twice. TryAddEnumerable
        // de-duplicates on the implementation type, which is both.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, S3Demo>(sp => sp.GetRequiredService<S3Demo>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IObjectStoreCapability, S3ObjectStore>(
                sp => sp.GetRequiredService<S3ObjectStore>()));

        return services;
    }
}
