namespace FlociLab.Core.Coverage;

/// <summary>
/// Reachability of the four emulator containers themselves, independent of any demo. This is what
/// makes /coverage useful on day one, before a single sample exists.
/// </summary>
public interface IEmulatorHealthProbe
{
    Task<EmulatorHealth> ProbeAsync(string provider, CancellationToken ct);

    /// <summary>
    /// Probes exactly the providers asked for, concurrently. The caller decides the set —
    /// <see cref="IDemoCatalog.CoveredProviders"/> in the case of /coverage — because a
    /// per-provider host must not report on clouds it carries no code for.
    /// </summary>
    Task<IReadOnlyList<EmulatorHealth>> ProbeAsync(IEnumerable<string> providers, CancellationToken ct);
}
