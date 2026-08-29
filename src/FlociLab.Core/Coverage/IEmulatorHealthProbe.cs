namespace FlociLab.Core.Coverage;

/// <summary>
/// Reachability of the four emulator containers themselves, independent of any demo. This is what
/// makes /coverage useful on day one, before a single sample exists.
/// </summary>
public interface IEmulatorHealthProbe
{
    Task<EmulatorHealth> ProbeAsync(string provider, CancellationToken ct);

    Task<IReadOnlyList<EmulatorHealth>> ProbeAllAsync(CancellationToken ct);
}
