using System.Reflection;

namespace FlociLab.Core.Coverage;

/// <summary>
/// Everything registered through an <c>Add*Demo()</c> extension, which is how the nav and the
/// coverage matrix discover samples without anything referencing them directly.
/// </summary>
public interface IDemoCatalog
{
    IReadOnlyList<IServiceDemo> Demos { get; }

    /// <summary>Demos grouped by provider, in <see cref="CloudProvider.All"/> order.</summary>
    IReadOnlyList<ProviderDemos> ByProvider { get; }

    /// <summary>
    /// Every assembly that owns routable pages: the registered demos' own assemblies, plus any
    /// declared through <c>AddPageAssembly</c>.
    ///
    /// <para>
    /// A host has to hand these to both <c>MapRazorComponents</c> (which builds the endpoint route
    /// table) and the <c>Router</c> component (which routes inside the interactive circuit) — a
    /// page reached by only one of the two either 404s on a fresh request or dead-ends on an
    /// in-app link. Both read this one property, so a new RCL cannot be wired into one and
    /// forgotten in the other; that was recorded as a live risk in docs/BLAZOR-PLAN.md §14 and
    /// this is the fix it called for.
    /// </para>
    /// </summary>
    IReadOnlyList<Assembly> PageAssemblies { get; }

    IServiceDemo? Find(string provider, string slug);
}
