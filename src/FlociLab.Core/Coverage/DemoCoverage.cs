namespace FlociLab.Core.Coverage;

/// <summary>A single cell of the /coverage grid.</summary>
public sealed record DemoCoverage(IServiceDemo Demo, ProbeResult Result);
