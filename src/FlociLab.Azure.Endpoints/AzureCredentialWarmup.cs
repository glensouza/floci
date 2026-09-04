using System.Diagnostics;
using Azure.Core;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlociLab.Azure;

/// <summary>
/// Acquires one Azure token in the background as the host starts, so no demo page pays for the
/// first one.
///
/// <para>
/// This exists for a measurement problem, not a performance one. <c>ManagedIdentityCredential</c>
/// probes for an available managed-identity source before it can issue a token, and the result of
/// that probe is cached for the process — so the *first* Azure call after startup carries it and
/// every later call does not. On the comparison pages, whose entire premise is one elapsed time
/// per provider per operation, that lands the whole cost in a single cell: measured 2026-09-04 on
/// the Secrets page, Azure's <c>SetSecret</c> read <b>49,942 ms</b> on the first run of a fresh
/// process and <b>7 ms</b> on the second, against AWS at 16 ms in the same column. A viewer reads
/// that as Azure being four orders of magnitude slower than AWS, which is false, and it is the
/// same class of false cloud-vs-cloud claim as the retired <c>localhost</c> row in
/// docs/BLAZOR-PLAN.md §14 — a per-process cost rendered as a per-operation one.
/// </para>
///
/// <para>
/// A <see cref="BackgroundService"/> rather than a plain <see cref="IHostedService"/>, and that is
/// load-bearing: the host awaits each hosted service's <c>StartAsync</c> before it listens, so
/// doing this work there would trade a 50 s cell for a 50 s startup — measured, when an earlier
/// draft did exactly that. <c>ExecuteAsync</c> is started and not awaited, so the warm-up races
/// the human reaching the page and the host serves immediately either way.
/// </para>
///
/// <para>
/// Deliberately not a correctness dependency: it never blocks or fails startup. If floci-az is
/// down the warm-up logs and returns, every page still renders, and the demo that runs against a
/// dead emulator still reports <c>Unreachable</c> on its own terms. The cost when the emulator is
/// down is bounded by <see cref="Budget"/> rather than by Azure.Identity's own retry policy.
/// </para>
/// </summary>
internal sealed class AzureCredentialWarmup(AzureEndpoints endpoints, ILogger<AzureCredentialWarmup> logger) : BackgroundService
{
    /// <summary>The audience every Key Vault plane in this lab asks for, and the one floci-az's IMDS endpoint names in the tokens it signs.</summary>
    private static readonly string[] WarmupScopes = ["https://vault.azure.net/.default"];

    /// <summary>
    /// Comfortably above the ~50 s the probe was measured at, because a budget that expires
    /// mid-probe is the worst of both worlds — it pays the wait and still leaves the cost on the
    /// page. Nothing waits on this, so a long budget costs only the background task.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(Budget);

        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            // The token itself is discarded. Azure.Identity caches the managed-identity source
            // probe process-wide, which is the part that costs ~50 s against a cold emulator, so
            // acquiring and throwing away one token is enough to take it off every later call.
            await endpoints.Credential().GetTokenAsync(new TokenRequestContext(WarmupScopes), cts.Token).ConfigureAwait(false);

            // Logged with the elapsed time because that number is the whole justification for this
            // class, and it is the one thing that would quietly stop being true if Azure.Identity
            // or floci-az changed. A warm-up that starts reporting single-digit milliseconds is a
            // signal that this can be deleted.
            logger.LogInformation(
                "Azure credential warmed up in {Elapsed:0} ms; the first Key Vault call will not carry the IMDS probe.",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Azure credential warm-up gave up after {Budget}; the first Key Vault call will carry the IMDS probe.", Budget);
        }
        catch (OperationCanceledException)
        {
            /* the host is shutting down mid-warm-up; there is nothing to warm and nothing to say */
        }
        catch (Exception ex)
        {
            // Swallowed on purpose, and broadly: floci-az being down — or answering something the
            // credential cannot parse — is a normal state for this lab, and it is the demo pages'
            // job to report that on their own terms, not startup's. Rethrowing would mean a host
            // that refuses to start without an emulator, which would make every page unreachable
            // to say that one page's provider is.
            logger.LogInformation(ex, "Azure credential warm-up did not complete; the first Key Vault call will carry the IMDS probe.");
        }
    }
}
