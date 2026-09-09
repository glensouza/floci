using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using FlociLab.Aws.CloudWatchMetrics;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Testcontainers.Floci;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator the
/// AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class AwsCloudWatchMetricsTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private CloudWatchMetricsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CloudWatchMetricsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CloudWatchMetricsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new CloudWatchMetricsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("PutMetricData", s.Title),
            s => Assert.Equal("GetMetricStatistics", s.Title),
            s => Assert.Equal("ListMetrics", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // The statistics read-back comes second, ahead of ListMetrics: it is the authoritative
        // proof of the round trip, and gating it behind the discovery call is what would abort a
        // healthy real-AWS run before it ever ran (docs/BLAZOR-PLAN.md §14). Assert.Collection
        // above pins that order.
        //
        // "including this run's value" is the postcondition, not the call merely succeeding: the
        // step throws unless the read-back statistics actually contained the value this run wrote.
        Assert.Contains("including this run's value", steps.Single(s => s.Title == "GetMetricStatistics").Response);
    }

    /// <summary>
    /// Re-runnable because every run tags its datapoint with a fresh RunId dimension. CloudWatch
    /// has no DeleteMetric API — a re-run is idempotent by not colliding with the previous run's
    /// data, not by cleaning it up.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice_Without_Colliding()
    {
        CloudWatchMetricsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    ///
    /// Cancelled mid-run rather than up front: a token that is already cancelled makes the very
    /// first SDK call throw, so no step is ever yielded and "no failed steps" holds vacuously. The
    /// assertion only has teeth once at least one step has been observed.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        CloudWatchMetricsDemo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);

                // Cancel once the run is genuinely under way, which is what the page does when
                // the user navigates away mid-round-trip.
                await cts.CancelAsync();
            }
        });

        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        CloudWatchMetricsDemo demo = new(new CloudWatchMetricsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// A run that fails at the first step still yields exactly that one failed step — no cleanup
    /// step to worry about, since CloudWatch Metrics has none.
    /// </summary>
    [Fact]
    public async Task Failed_Run_Yields_Only_The_Step_It_Reached()
    {
        CloudWatchMetricsDemo demo = new(new CloudWatchMetricsClientFactory(EndpointsFor("http://127.0.0.1:1")));
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(steps, s => Assert.Equal("PutMetricData", s.Title));
        Assert.All(steps, s => Assert.False(s.Succeeded));
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
