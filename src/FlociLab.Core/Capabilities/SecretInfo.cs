namespace FlociLab.Core.Capabilities;

/// <summary>A stored secret's metadata. The value is fetched separately, never listed.</summary>
public sealed record SecretInfo(string Name, string? Version = null, DateTimeOffset? UpdatedAt = null);
