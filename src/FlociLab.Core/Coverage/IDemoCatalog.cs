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
    /// The providers this host actually covers, in <see cref="CloudProvider.All"/> order — the
    /// ones with a registered demo, or all four when none are registered.
    ///
    /// <para>
    /// A per-provider host references exactly one sample RCL, so probing
    /// <see cref="CloudProvider.All"/> would have it report on three clouds it carries no code
    /// for: in a clone with only that provider's emulator running, /coverage renders three red
    /// "Unreachable" rows and the sample reads as broken. The empty case still means all four, so
    /// a host with no demos yet — Phase 0's exit criterion — keeps proving the emulators are up.
    /// </para>
    /// </summary>
    IReadOnlyList<string> CoveredProviders { get; }

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
