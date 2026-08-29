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

    IServiceDemo? Find(string provider, string slug);
}

public sealed record ProviderDemos(string Provider, IReadOnlyList<IServiceDemo> Demos)
{
    public string DisplayName => CloudProvider.DisplayName(Provider);
}

internal sealed class DemoCatalog : IDemoCatalog
{
    public DemoCatalog(IEnumerable<IServiceDemo> demos)
    {
        Demos = [.. demos
            .OrderBy(d => CloudProvider.Order(d.Provider))
            .ThenBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)];

        ByProvider = [.. CloudProvider.All
            .Select(p => new ProviderDemos(p, [.. Demos.Where(d => d.Provider == p)]))
            .Where(g => g.Demos.Count > 0)];
    }

    public IReadOnlyList<IServiceDemo> Demos { get; }

    public IReadOnlyList<ProviderDemos> ByProvider { get; }

    public IServiceDemo? Find(string provider, string slug)
        => Demos.FirstOrDefault(d =>
            string.Equals(d.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
