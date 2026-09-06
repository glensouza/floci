using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using FlociLab.Aws.EventBridge;
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
public sealed class AwsEventBridgeTests : IAsyncLifetime
{
    // Same reasoning as AwsSsmTests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private EventBridgeClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new EventBridgeClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new EventBridgeDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new EventBridgeDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateEventBus", s.Title),
            s => Assert.Equal("PutRule", s.Title),
            s => Assert.Equal("PutTargets", s.Title),
            s => Assert.Equal("DescribeRule", s.Title),
            s => Assert.Equal("PutEvents", s.Title),
            s => Assert.Equal("ListTargetsByRule", s.Title),
            s => Assert.Equal("RemoveTargets — cleanup", s.Title),
            s => Assert.Equal("DeleteRule — cleanup", s.Title),
            s => Assert.Equal("DeleteEventBus — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("1 target(s)", steps.Single(s => s.Title == "ListTargetsByRule").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Buses_Behind()
    {
        EventBridgeDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonEventBridge client = this.factory.Create();
        ListEventBusesResponse before = await client.ListEventBusesAsync(new ListEventBusesRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListEventBusesResponse after = await client.ListEventBusesAsync(new ListEventBusesRequest(), ct);

        Assert.Equal(
            before.EventBuses.Select(b => b.Name).Order(),
            after.EventBuses.Select(b => b.Name).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render nine red steps blaming the
    /// emulator for the user leaving.
    ///
    /// Cancelled mid-run rather than up front: a token that is already cancelled makes the very
    /// first SDK call throw, so no step is ever yielded and "no failed steps" holds vacuously. The
    /// assertion only has teeth once at least one step has been observed.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        EventBridgeDemo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);

                // Cancel once the run is genuinely under way, which is what the page does when the
                // user navigates away mid-round-trip.
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
        EventBridgeDemo demo = new(new EventBridgeClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
