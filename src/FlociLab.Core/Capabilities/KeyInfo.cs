namespace FlociLab.Core.Capabilities;

/// <summary>A managed key. <see cref="Id"/> is the provider's own identifier — ARN, key URI, OCID.</summary>
public sealed record KeyInfo(string Id, string? Name = null, string? Algorithm = null);
