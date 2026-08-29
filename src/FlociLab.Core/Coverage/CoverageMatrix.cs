using System.Diagnostics;
using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Coverage;

/// <summary>A single cell of the /coverage grid.</summary>
public sealed record DemoCoverage(IServiceDemo Demo, ProbeResult Result);

/// <summary>
/// Calls <see cref="IServiceDemo.ProbeAsync"/> on every registered demo in parallel. This is how
/// the checklists in docs/BLAZOR-PLAN.md §13 stay honest — the app reports what the emulators
/// actually do, rather than what the plan hoped they would.
/// </summary>
public interface ICoverageMatrix
{
    Task<IReadOnlyList<DemoCoverage>> ProbeAllAsync(CancellationToken ct);
}

internal sealed class CoverageMatrix(IDemoCatalog catalog, IOptions<FlociOptions> options) : ICoverageMatrix
{
    public async Task<IReadOnlyList<DemoCoverage>> ProbeAllAsync(CancellationToken ct)
        => await Task.WhenAll(catalog.Demos.Select(d => ProbeAsync(d, ct))).ConfigureAwait(false);

    private async Task<DemoCoverage> ProbeAsync(IServiceDemo demo, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.ProbeTimeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await demo.ProbeAsync(timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            // A demo may not bother timing itself; fill it in so every cell shows a duration.
            return new DemoCoverage(demo, result.Duration is null ? result with { Duration = stopwatch.Elapsed } : result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new DemoCoverage(demo, ProbeResult.Unreachable(
                $"No response within {options.Value.ProbeTimeout.TotalSeconds:0.#}s.", stopwatch.Elapsed));
        }
        // The caller giving up is not a probe outcome. Letting it propagate is what tells
        // /coverage apart "navigated away mid-probe" from "the emulator is down" — the timeout
        // above is the only cancellation this method owns.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new DemoCoverage(demo, ProbeResult.FromException(ex, stopwatch.Elapsed));
        }
    }
}
