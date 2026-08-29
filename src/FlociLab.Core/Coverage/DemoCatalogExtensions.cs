using System.Reflection;

namespace FlociLab.Core.Coverage;

public static class DemoCatalogExtensions
{
    /// <summary>
    /// The assemblies that own the registered demos' pages. Each sample RCL carries its own
    /// routable page, so a host has to hand these to both <c>MapRazorComponents</c> (which builds
    /// the endpoint route table) and the <c>Router</c> component (which routes inside the
    /// interactive circuit) — a page reached by only one of the two either 404s on a fresh
    /// request or dead-ends on an in-app link.
    ///
    /// Derived from the catalog rather than listed by hand so that a host gains a sample's routes
    /// from the same single <c>Add*Demo()</c> call that registers it. With ~136 samples planned,
    /// a hand-maintained assembly list silently 404s the first time someone forgets a line.
    /// </summary>
    public static Assembly[] SampleAssemblies(this IDemoCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return [.. catalog.Demos.Select(d => d.GetType().Assembly).Distinct()];
    }
}
