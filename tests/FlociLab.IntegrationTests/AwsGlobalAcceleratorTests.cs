using Amazon.GlobalAccelerator;
using Amazon.GlobalAccelerator.Model;
using FlociLab.Aws.GlobalAccelerator;
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
public sealed class AwsGlobalAcceleratorTests : IAsyncLifetime
{
    // Same reasoning as AwsRoute53Tests: pinned to :latest so the tripwire tracks the same build
    // the AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private GlobalAcceleratorClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new GlobalAcceleratorClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new GlobalAcceleratorDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new GlobalAcceleratorDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListAccelerators — before", s.Title),
            s => Assert.Equal("CreateAccelerator", s.Title),
            s => Assert.Equal("DescribeAccelerator — static addresses", s.Title),
            s => Assert.Equal("DescribeAcceleratorAttributes", s.Title),
            s => Assert.Equal("CreateListener", s.Title),
            s => Assert.Equal("UpdateListener", s.Title),
            s => Assert.Equal("CreateEndpointGroup", s.Title),
            s => Assert.Equal("AddEndpoints", s.Title),
            s => Assert.Equal("UpdateEndpointGroup", s.Title),
            s => Assert.Equal("DescribeEndpointGroup", s.Title),
            s => Assert.Equal("TagResource", s.Title),
            s => Assert.Equal("ListTagsForResource", s.Title),
            s => Assert.Equal("DeleteAccelerator — refused while enabled", s.Title),
            s => Assert.Equal("DeleteListener — refused while it has an endpoint group", s.Title),
            s => Assert.Equal("RemoveEndpoints", s.Title),
            s => Assert.Equal("DeleteEndpointGroup", s.Title),
            s => Assert.Equal("DeleteListener", s.Title),
            s => Assert.Equal("UpdateAccelerator — disable", s.Title),
            s => Assert.Equal("DeleteAccelerator", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        GlobalAcceleratorDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonGlobalAccelerator client = this.factory.Create();
        ListAcceleratorsResponse before = await client.ListAcceleratorsAsync(new ListAcceleratorsRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListAcceleratorsResponse after = await client.ListAcceleratorsAsync(new ListAcceleratorsRequest(), ct);

        Assert.Equal((before.Accelerators ?? []).Select(a => a.Name).Order(), (after.Accelerators ?? []).Select(a => a.Name).Order());
    }

    /// <summary>
    /// The cleanup path on its own: a run stopped after the create — the consumer walking away, as
    /// the page does on dispose — still has an accelerator with a listener and an endpoint group
    /// under it, and cleanup has to find it by name and take the whole tree apart bottom-up.
    /// </summary>
    [Fact]
    public async Task Abandoned_Run_Is_Cleaned_Up_By_Name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonGlobalAccelerator client = this.factory.Create();
        ListAcceleratorsResponse before = await client.ListAcceleratorsAsync(new ListAcceleratorsRequest(), ct);

        List<DemoStep> steps = [];

        // Stop after CreateEndpointGroup (seven steps in): the accelerator is enabled and holds a
        // listener and an endpoint group, the state in which a naive delete is refused twice over.
        await foreach (DemoStep step in new GlobalAcceleratorDemo(this.factory).RunAsync(ct))
        {
            steps.Add(step);

            if (step.Title == "CreateEndpointGroup")
            {
                break;
            }
        }

        Assert.Equal("CreateEndpointGroup", steps[^1].Title);

        ListAcceleratorsResponse after = await client.ListAcceleratorsAsync(new ListAcceleratorsRequest(), ct);

        Assert.Equal((before.Accelerators ?? []).Select(a => a.Name).Order(), (after.Accelerators ?? []).Select(a => a.Name).Order());
    }

    /// <summary>
    /// floci enforces the deletion order real Global Accelerator does — the two refusals the sample
    /// shows — and one endpoint group per region per listener, but does not enforce <c>IdempotencyToken</c> or reject overlapping listener port
    /// ranges (docs/BLAZOR-PLAN.md §14). When either assertion here starts failing, upstream fixed
    /// it: add the replayed-token and overlapping-range steps to the sample.
    /// </summary>
    [Fact]
    public async Task Emulator_Behaviours_The_Sample_Documents()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonGlobalAccelerator client = this.factory.Create();
        string token = Guid.NewGuid().ToString();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string? firstArn = null;
        string? secondArn = null;

        try
        {
            CreateAcceleratorResponse first = await client.CreateAcceleratorAsync(new CreateAcceleratorRequest { Name = $"flocilab-idem-a-{suffix}", IdempotencyToken = token }, ct);
            firstArn = first.Accelerator.AcceleratorArn;

            // Real Global Accelerator returns the first accelerator for a replayed token; floci
            // creates a second one.
            CreateAcceleratorResponse second = await client.CreateAcceleratorAsync(new CreateAcceleratorRequest { Name = $"flocilab-idem-b-{suffix}", IdempotencyToken = token }, ct);
            secondArn = second.Accelerator.AcceleratorArn;

            Assert.NotEqual(firstArn, secondArn);

            CreateListenerResponse listener = await client.CreateListenerAsync(
                new CreateListenerRequest { AcceleratorArn = firstArn, Protocol = Protocol.TCP, PortRanges = [new PortRange { FromPort = 80, ToPort = 80 }], IdempotencyToken = Guid.NewGuid().ToString() }, ct);

            // Real Global Accelerator answers InvalidPortRangeException for a range that overlaps
            // another listener's; floci accepts it.
            CreateListenerResponse overlapping = await client.CreateListenerAsync(
                new CreateListenerRequest { AcceleratorArn = firstArn, Protocol = Protocol.TCP, PortRanges = [new PortRange { FromPort = 80, ToPort = 90 }], IdempotencyToken = Guid.NewGuid().ToString() }, ct);

            Assert.False(string.IsNullOrEmpty(overlapping.Listener.ListenerArn));
            Assert.NotEqual(listener.Listener.ListenerArn, overlapping.Listener.ListenerArn);

            // Enforced as real Global Accelerator does: one endpoint group per region per listener.
            await client.CreateEndpointGroupAsync(
                new CreateEndpointGroupRequest { ListenerArn = listener.Listener.ListenerArn, EndpointGroupRegion = "us-east-1", IdempotencyToken = Guid.NewGuid().ToString() }, ct);

            await Assert.ThrowsAsync<EndpointGroupAlreadyExistsException>(() => client.CreateEndpointGroupAsync(
                new CreateEndpointGroupRequest { ListenerArn = listener.Listener.ListenerArn, EndpointGroupRegion = "us-east-1", IdempotencyToken = Guid.NewGuid().ToString() }, ct));
        }
        finally
        {
            // Distinct: when upstream honours the token both ARNs are the same accelerator, and a
            // second delete would bury the tripwire's assertion under AcceleratorNotFoundException.
            foreach (string arn in ((string?[])[firstArn, secondArn]).OfType<string>().Distinct())
            {
                ListListenersResponse listeners = await client.ListListenersAsync(new ListListenersRequest { AcceleratorArn = arn }, CancellationToken.None);

                foreach (Listener listener in listeners.Listeners ?? [])
                {
                    ListEndpointGroupsResponse groups = await client.ListEndpointGroupsAsync(new ListEndpointGroupsRequest { ListenerArn = listener.ListenerArn }, CancellationToken.None);

                    foreach (EndpointGroup group in groups.EndpointGroups ?? [])
                    {
                        await client.DeleteEndpointGroupAsync(new DeleteEndpointGroupRequest { EndpointGroupArn = group.EndpointGroupArn }, CancellationToken.None);
                    }

                    await client.DeleteListenerAsync(new DeleteListenerRequest { ListenerArn = listener.ListenerArn }, CancellationToken.None);
                }

                await client.UpdateAcceleratorAsync(new UpdateAcceleratorRequest { AcceleratorArn = arn, Enabled = false }, CancellationToken.None);
                await client.DeleteAcceleratorAsync(new DeleteAcceleratorRequest { AcceleratorArn = arn }, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        GlobalAcceleratorDemo demo = new(this.factory);
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
        GlobalAcceleratorDemo demo = new(new GlobalAcceleratorClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
