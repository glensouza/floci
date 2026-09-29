using Amazon.Route53;
using Amazon.Route53.Model;
using FlociLab.Aws.Route53;
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
public sealed class AwsRoute53Tests : IAsyncLifetime
{
    // Same reasoning as AwsSsmTests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private Route53ClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new Route53ClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new Route53Demo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new Route53Demo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListHostedZones — before", s.Title),
            s => Assert.Equal("CreateHostedZone", s.Title),
            s => Assert.Equal("CreateHostedZone — repeated CallerReference", s.Title),
            s => Assert.Equal("ChangeResourceRecordSets — UPSERT", s.Title),
            s => Assert.Equal("ListResourceRecordSets", s.Title),
            s => Assert.Equal("GetChange", s.Title),
            s => Assert.Equal("DeleteHostedZone — while it holds a record", s.Title),
            s => Assert.Equal("ChangeResourceRecordSets — DELETE", s.Title),
            s => Assert.Equal("DeleteHostedZone — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // Ids come back as "/hostedzone/Z…" and "/change/C…"; pasting one whole into the displayed
        // path shows a URL the SDK never sent.
        Assert.All(steps, s => Assert.DoesNotContain("//", (s.Request ?? string.Empty).Replace("http://", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.Contains("192.0.2.10", steps.Single(s => s.Title == "ListResourceRecordSets").Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("HostedZoneAlreadyExists", steps.Single(s => s.Title == "CreateHostedZone — repeated CallerReference").Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("HostedZoneNotEmpty", steps.Single(s => s.Title == "DeleteHostedZone — while it holds a record").Response ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Zones_Behind()
    {
        Route53Demo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonRoute53 client = this.factory.Create();
        ListHostedZonesResponse before = await client.ListHostedZonesAsync(new ListHostedZonesRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListHostedZonesResponse after = await client.ListHostedZonesAsync(new ListHostedZonesRequest(), ct);

        Assert.Equal(
            (before.HostedZones ?? []).Select(z => z.Id).Order(),
            (after.HostedZones ?? []).Select(z => z.Id).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        Route53Demo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);
            }
        });

        Assert.DoesNotContain(steps, s => !s.Succeeded);
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        Route53Demo demo = new(new Route53ClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
