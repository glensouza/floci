using FlociLab.Comparison.Pages;
using FlociLab.Core;
using Microsoft.Extensions.DependencyInjection;

namespace FlociLab.Comparison;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the side-by-side comparison pages to a host. There is nothing to register but the
    /// routes: these pages inject <c>IEnumerable&lt;ICloudCapability&gt;</c>, which each sample's
    /// own <c>Add*Demo()</c> has already filled in, so this RCL owns no services of its own.
    ///
    /// <para>
    /// It still has to be called, because an RCL with no <see cref="IServiceDemo"/> is invisible
    /// to the catalog's assembly discovery — the case docs/BLAZOR-PLAN.md §14 flagged.
    /// </para>
    /// </summary>
    public static IServiceCollection AddComparisonPages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddPageAssembly(typeof(ObjectStoragePage).Assembly);
    }
}
