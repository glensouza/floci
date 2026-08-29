using System.Net;
using System.Net.Sockets;

namespace FlociLab.Core;

public sealed record ProbeResult(
    ProbeStatus Status,
    string? Detail = null,
    TimeSpan? Duration = null)
{
    public static ProbeResult Ok(TimeSpan? duration = null, string? detail = null)
        => new(ProbeStatus.Ok, detail, duration);

    public static ProbeResult NotImplemented(string? detail = null, TimeSpan? duration = null)
        => new(ProbeStatus.NotImplemented, detail, duration);

    public static ProbeResult Unreachable(string? detail = null, TimeSpan? duration = null)
        => new(ProbeStatus.Unreachable, detail, duration);

    public static ProbeResult Error(string detail, TimeSpan? duration = null)
        => new(ProbeStatus.Error, detail, duration);

    /// <summary>
    /// Transport-level classification, for the cases every provider shares: a refused connection
    /// or a timeout is <see cref="ProbeStatus.Unreachable"/>, anything else is
    /// <see cref="ProbeStatus.Error"/>. Samples handle their own SDK's 501 equivalent first and
    /// fall back to this — it cannot see a 501 hiding inside an SDK exception type it doesn't know.
    /// </summary>
    public static ProbeResult FromException(Exception ex, TimeSpan? duration = null) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotImplemented }
            => NotImplemented(ex.Message, duration),
        // Any status code at all means the connection succeeded and the emulator answered, so
        // this is the emulator behaving badly — never Unreachable, which claims it isn't running.
        HttpRequestException { StatusCode: not null }
            => Error(Describe(ex), duration),
        HttpRequestException or SocketException or TimeoutException or TaskCanceledException
            => Unreachable(Describe(ex), duration),
        _ => Error(Describe(ex), duration),
    };

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
