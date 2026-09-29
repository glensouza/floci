using Amazon.ElasticLoadBalancingV2;
using Amazon.ElasticLoadBalancingV2.Model;
using FlociLab.Aws.ElbV2;
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
public sealed class AwsElbV2Tests : IAsyncLifetime
{
    // Same reasoning as AwsRoute53Tests: pinned to :latest so the tripwire tracks the same build
    // the AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private ElbV2ClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new ElbV2ClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new ElbV2Demo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new ElbV2Demo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("DescribeLoadBalancers — before", s.Title),
            s => Assert.Equal("CreateTargetGroup", s.Title),
            s => Assert.Equal("CreateLoadBalancer", s.Title),
            s => Assert.Equal("RegisterTargets", s.Title),
            s => Assert.Equal("DescribeTargetHealth", s.Title),
            s => Assert.Equal("CreateListener", s.Title),
            s => Assert.Equal("CreateRule", s.Title),
            s => Assert.Equal("DescribeRules", s.Title),
            s => Assert.Equal("ModifyLoadBalancerAttributes", s.Title),
            s => Assert.Equal("DescribeLoadBalancerAttributes", s.Title),
            s => Assert.Equal("AddTags", s.Title),
            s => Assert.Equal("DescribeTags", s.Title),
            s => Assert.Equal("DeleteTargetGroup — while in use", s.Title),
            s => Assert.Equal("DeregisterTargets", s.Title),
            s => Assert.Equal("DeleteLoadBalancer", s.Title),
            s => Assert.Equal("DeleteTargetGroup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("ResourceInUse", steps.Single(s => s.Title == "DeleteTargetGroup — while in use").Response ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        ElbV2Demo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonElasticLoadBalancingV2 client = this.factory.Create();
        DescribeLoadBalancersResponse loadBalancersBefore = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest(), ct);
        DescribeTargetGroupsResponse targetGroupsBefore = await client.DescribeTargetGroupsAsync(new DescribeTargetGroupsRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        DescribeLoadBalancersResponse loadBalancersAfter = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest(), ct);
        DescribeTargetGroupsResponse targetGroupsAfter = await client.DescribeTargetGroupsAsync(new DescribeTargetGroupsRequest(), ct);

        Assert.Equal((loadBalancersBefore.LoadBalancers ?? []).Select(l => l.LoadBalancerArn).Order(), (loadBalancersAfter.LoadBalancers ?? []).Select(l => l.LoadBalancerArn).Order());
        Assert.Equal((targetGroupsBefore.TargetGroups ?? []).Select(t => t.TargetGroupArn).Order(), (targetGroupsAfter.TargetGroups ?? []).Select(t => t.TargetGroupArn).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        ElbV2Demo demo = new(this.factory);
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
        ElbV2Demo demo = new(new ElbV2ClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
