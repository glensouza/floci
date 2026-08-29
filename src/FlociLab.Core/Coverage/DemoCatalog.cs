namespace FlociLab.Core.Coverage;

internal sealed class DemoCatalog : IDemoCatalog
{
    public DemoCatalog(IEnumerable<IServiceDemo> demos)
    {
        this.Demos = [.. demos
            .OrderBy(d => CloudProvider.Order(d.Provider))
            .ThenBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)];

        this.ByProvider = [.. CloudProvider.All
            .Select(p => new ProviderDemos(p, [.. this.Demos.Where(d => d.Provider == p)]))
            .Where(g => g.Demos.Count > 0)];
    }

    public IReadOnlyList<IServiceDemo> Demos { get; }

    public IReadOnlyList<ProviderDemos> ByProvider { get; }

    public IServiceDemo? Find(string provider, string slug)
        => this.Demos.FirstOrDefault(d =>
            string.Equals(d.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
