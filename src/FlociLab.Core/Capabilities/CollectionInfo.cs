namespace FlociLab.Core.Capabilities;

/// <summary>A table / container / collection, depending on the provider's vocabulary.</summary>
public sealed record CollectionInfo(string Name, long? ItemCount = null);
