using System.Diagnostics;
using System.Net;
using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Coverage;

/// <summary>One emulator's answer to GET /_floci/health.</summary>
public sealed record EmulatorHealth(string Provider, string Endpoint, ProbeResult Result);

/// <summary>
/// Reachability of the four emulator containers themselves, independent of any demo. This is what
/// makes /coverage useful on day one, before a single sample exists.
/// </summary>
public interface IEmulatorHealthProbe
{
    Task<EmulatorHealth> ProbeAsync(string provider, CancellationToken ct);

    Task<IReadOnlyList<EmulatorHealth>> ProbeAllAsync(CancellationToken ct);
}

internal sealed class HttpEmulatorHealthProbe(
    IHttpClientFactory httpClientFactory,
    IOptions<FlociOptions> options) : IEmulatorHealthProbe
{
    internal const string HttpClientName = "floci-health";

    public async Task<EmulatorHealth> ProbeAsync(string provider, CancellationToken ct)
    {
        var settings = options.Value.For(provider);
        var url = $"{settings.Endpoint.TrimEnd('/')}/{settings.HealthPath.TrimStart('/')}";
        var client = httpClientFactory.CreateClient(HttpClientName);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            stopwatch.Stop();

            var detail = Summarise(body);

            return new EmulatorHealth(provider, settings.Endpoint, response.StatusCode switch
            {
                HttpStatusCode.OK => ProbeResult.Ok(stopwatch.Elapsed, detail),
                HttpStatusCode.NotImplemented => ProbeResult.NotImplemented(detail, stopwatch.Elapsed),
                _ => ProbeResult.Error($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", stopwatch.Elapsed),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EmulatorHealth(provider, settings.Endpoint, ProbeResult.FromException(ex, stopwatch.Elapsed));
        }
    }

    public async Task<IReadOnlyList<EmulatorHealth>> ProbeAllAsync(CancellationToken ct)
        => await Task.WhenAll(CloudProvider.All.Select(p => ProbeAsync(p, ct))).ConfigureAwait(false);

    /// <summary>Health payloads vary per image; show the first line rather than pretending to parse them.</summary>
    private static string? Summarise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var line = body.Trim().Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
