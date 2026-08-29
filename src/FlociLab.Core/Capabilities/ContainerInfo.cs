namespace FlociLab.Core.Capabilities;

/// <summary>A bucket / container, in whatever the provider calls it.</summary>
public sealed record ContainerInfo(string Name, DateTimeOffset? CreatedAt = null);
