using System.Reflection;

namespace FlociLab.Core.Coverage;

/// <summary>
/// Registers an assembly as owning routable pages when nothing else would reveal it.
///
/// <para>
/// A sample RCL is discovered through the <see cref="IServiceDemo"/> it registers, so its pages
/// come along for free. An RCL that contributes pages but no demo — FlociLab.Comparison, whose
/// pages consume capability interfaces (docs/BLAZOR-PLAN.md §8) — is invisible to that, and its
/// routes have to be declared. This is that declaration, and it exists so there is still exactly
/// one list of routable assemblies rather than one per host wiring point.
/// </para>
/// </summary>
public sealed record PageAssembly(Assembly Assembly);
