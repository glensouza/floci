namespace FlociLab.Core.Coverage;

/// <summary>One provider's demos, as the nav renders them.</summary>
public sealed record ProviderDemos(string Provider, IReadOnlyList<IServiceDemo> Demos)
{
    public string DisplayName => CloudProvider.DisplayName(this.Provider);
}
