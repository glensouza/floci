using System.Diagnostics;
using System.Net;
using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Coverage;

internal sealed class HttpEmulatorHealthProbe(IHttpClientFactory httpClientFactory, IOptions<FlociOptions> options) : IEmulatorHealthProbe
{
    internal const string HttpClientName = "floci-health";

    public async Task<EmulatorHealth> ProbeAsync(string provider, CancellationToken ct)
    {
        EmulatorOptions settings = options.Value.For(provider);
        string url = $"{settings.Endpoint.TrimEnd('/')}/{settings.HealthPath.TrimStart('/')}";
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using HttpResponseMessage response = await client.GetAsync(url, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            stopwatch.Stop();

            string? detail = Summarise(body);

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

    public async Task<IReadOnlyList<EmulatorHealth>> ProbeAsync(IEnumerable<string> providers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);

        return await Task.WhenAll(providers.Select(p => this.ProbeAsync(p, ct))).ConfigureAwait(false);
    }

    /// <summary>Health payloads vary per image; show the first line rather than pretending to parse them.</summary>
    private static string? Summarise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string line = body.Trim().Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
