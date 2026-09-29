using Amazon.ServiceDiscovery;
using Amazon.ServiceDiscovery.Model;
using FlociLab.Aws.CloudMap;
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
public sealed class AwsCloudMapTests : IAsyncLifetime
{
    // Same reasoning as AwsSsmTests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private CloudMapClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CloudMapClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CloudMapDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new CloudMapDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListNamespaces — before", s.Title),
            s => Assert.Equal("CreateHttpNamespace", s.Title),
            s => Assert.Equal("GetOperation — namespace created", s.Title),
            s => Assert.Equal("CreateService", s.Title),
            s => Assert.Equal("RegisterInstance", s.Title),
            s => Assert.Equal("ListInstances", s.Title),
            s => Assert.Equal("DiscoverInstances — stage=blue", s.Title),
            s => Assert.Equal("DiscoverInstances — no match", s.Title),
            s => Assert.Equal("DeleteNamespace — while it holds a service", s.Title),
            s => Assert.Equal("DeregisterInstance", s.Title),
            s => Assert.Equal("DeleteService", s.Title),
            s => Assert.Equal("DeleteNamespace — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        Assert.Contains("192.0.2.10", steps.Single(s => s.Title == "ListInstances").Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("web-1", steps.Single(s => s.Title == "DiscoverInstances — stage=blue").Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("ResourceInUse", steps.Single(s => s.Title == "DeleteNamespace — while it holds a service").Response ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Namespaces_Behind()
    {
        CloudMapDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonServiceDiscovery client = this.factory.Create();
        ListNamespacesResponse before = await client.ListNamespacesAsync(new ListNamespacesRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListNamespacesResponse after = await client.ListNamespacesAsync(new ListNamespacesRequest(), ct);

        Assert.Equal(
            (before.Namespaces ?? []).Select(n => n.Id).Order(),
            (after.Namespaces ?? []).Select(n => n.Id).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        CloudMapDemo demo = new(this.factory);
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
        CloudMapDemo demo = new(new CloudMapClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
