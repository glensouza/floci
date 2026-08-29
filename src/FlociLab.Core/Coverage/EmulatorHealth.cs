namespace FlociLab.Core.Coverage;

/// <summary>One emulator's answer to GET /_floci/health.</summary>
public sealed record EmulatorHealth(string Provider, string Endpoint, ProbeResult Result);
