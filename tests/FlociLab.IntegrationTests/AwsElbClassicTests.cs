using Amazon.ElasticLoadBalancing;
using Amazon.ElasticLoadBalancing.Model;
using FlociLab.Aws.ElbClassic;
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
public sealed class AwsElbClassicTests : IAsyncLifetime
{
    // Same reasoning as AwsRoute53Tests: pinned to :latest so the tripwire tracks the same build
    // the AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private ElbClassicClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new ElbClassicClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new ElbClassicDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new ElbClassicDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("DescribeLoadBalancers — before", s.Title),
            s => Assert.Equal("CreateLoadBalancer", s.Title),
            s => Assert.Equal("CreateLoadBalancerListeners", s.Title),
            s => Assert.Equal("ConfigureHealthCheck", s.Title),
            s => Assert.Equal("RegisterInstancesWithLoadBalancer", s.Title),
            s => Assert.Equal("DescribeInstanceHealth", s.Title),
            s => Assert.Equal("DescribeLoadBalancers — after configuring", s.Title),
            s => Assert.Equal("ModifyLoadBalancerAttributes", s.Title),
            s => Assert.Equal("DescribeLoadBalancerAttributes", s.Title),
            s => Assert.Equal("AddTags", s.Title),
            s => Assert.Equal("DescribeTags", s.Title),
            s => Assert.Equal("DeleteLoadBalancerListeners", s.Title),
            s => Assert.Equal("DeregisterInstancesFromLoadBalancer", s.Title),
            s => Assert.Equal("DeleteLoadBalancer", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        ElbClassicDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonElasticLoadBalancing client = this.factory.Create();
        DescribeLoadBalancersResponse loadBalancersBefore = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        DescribeLoadBalancersResponse loadBalancersAfter = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest(), ct);

        Assert.Equal((loadBalancersBefore.LoadBalancerDescriptions ?? []).Select(l => l.LoadBalancerName).Order(), (loadBalancersAfter.LoadBalancerDescriptions ?? []).Select(l => l.LoadBalancerName).Order());
    }

    /// <summary>
    /// The two behaviours the cleanup step rests on: DeleteLoadBalancer answers success for a name
    /// that never existed, so it cannot be the postcondition, and DescribeLoadBalancers answers
    /// LoadBalancerNotFound, so it can.
    /// </summary>
    [Fact]
    public async Task Delete_Is_Idempotent_But_Describe_Reports_A_Missing_Load_Balancer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-never-{Guid.NewGuid().ToString("N")[..8]}";

        using IAmazonElasticLoadBalancing client = this.factory.Create();

        await client.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest { LoadBalancerName = name }, ct);

        await Assert.ThrowsAsync<AccessPointNotFoundException>(() => client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest { LoadBalancerNames = [name] }, ct));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        ElbClassicDemo demo = new(this.factory);
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
        ElbClassicDemo demo = new(new ElbClassicClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
