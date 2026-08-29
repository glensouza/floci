namespace FlociLab.Core;

/// <summary>
/// The four outcomes the coverage matrix depends on. <see cref="NotImplemented"/> is a
/// legitimate, documented result — an emulator that returns 501 for a service is recorded
/// as such rather than worked around (docs/BLAZOR-PLAN.md §10).
/// </summary>
public enum ProbeStatus
{
    /// <summary>The call succeeded.</summary>
    Ok,

    /// <summary>HTTP 501, or the SDK equivalent: the emulator does not implement this yet.</summary>
    NotImplemented,

    /// <summary>Connection refused or timed out — the emulator is not running.</summary>
    Unreachable,

    /// <summary>Anything else. The message goes in <see cref="ProbeResult.Detail"/>.</summary>
    Error,
}
