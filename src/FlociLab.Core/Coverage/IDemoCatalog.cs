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
