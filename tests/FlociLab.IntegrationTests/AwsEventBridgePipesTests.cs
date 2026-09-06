using Amazon.Pipes;
using Amazon.Pipes.Model;
using FlociLab.Aws.EventBridgePipes;
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
public sealed class AwsEventBridgePipesTests : IAsyncLifetime
{
    // Same reasoning as AwsSsmTests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private EventBridgePipesClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new EventBridgePipesClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new EventBridgePipesDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new EventBridgePipesDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreatePipe", s.Title),
            s => Assert.Equal("DescribePipe", s.Title),
            s => Assert.Equal("ListPipes", s.Title),
            s => Assert.Equal("StopPipe", s.Title),
            s => Assert.Equal("StartPipe", s.Title),
            s => Assert.Equal("DeletePipe — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // "including" is the postcondition, not the count: the step throws unless the listing
        // actually contained the pipe this run created (§14).
        Assert.Contains("including flocilab-pipe-", steps.Single(s => s.Title == "ListPipes").Response);
    }

    /// <summary>
    /// Pins the emulator behaviour the Stop and Start steps rest on. floci settles a pipe's state
    /// synchronously — <c>CreatePipe</c> answers <c>RUNNING</c>, not the <c>CREATING</c> real Pipes
    /// answers — so the demo's linear Create → Stop → Start → Delete run needs no state wait
    /// (§14). Against real Pipes those steps would race the transition and get
    /// <c>ConflictException</c>. If floci ever becomes asynchronous here, this fails first and
    /// says so, rather than the round-trip going intermittently red.
    /// </summary>
    [Fact]
    public async Task Floci_Settles_Pipe_State_Synchronously()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-pipe-state-{Guid.NewGuid():N}";

        using IAmazonPipes client = this.factory.Create();

        CreatePipeResponse created = await client.CreatePipeAsync(
            new CreatePipeRequest
            {
                Name = name,
                RoleArn = "arn:aws:iam::000000000000:role/flocilab-pipes-role",
                Source = $"arn:aws:sqs:{this.factory.Region}:000000000000:src",
                Target = $"arn:aws:sqs:{this.factory.Region}:000000000000:tgt",
            }, ct);

        try
        {
            Assert.Equal(PipeState.RUNNING, created.CurrentState);

            StopPipeResponse stopped = await client.StopPipeAsync(new StopPipeRequest { Name = name }, ct);
            Assert.Equal(PipeState.STOPPED, stopped.CurrentState);

            StartPipeResponse started = await client.StartPipeAsync(new StartPipeRequest { Name = name }, ct);
            Assert.Equal(PipeState.RUNNING, started.CurrentState);
        }
        finally
        {
            await client.DeletePipeAsync(new DeletePipeRequest { Name = name }, CancellationToken.None);
        }
    }

    /// <summary>
    /// The other half of the cleanup story: the demo's <c>NotFoundException</c> catch reports
    /// "the pipe was never created" as a *success*, which is only truthful because a 404 is proof
    /// nothing exists. If floci ever answers 200 to a delete of a missing pipe, that green badge
    /// becomes a lie and this test is what says so.
    /// </summary>
    [Fact]
    public async Task Deleting_A_Pipe_That_Was_Never_Created_Answers_NotFound()
    {
        using IAmazonPipes client = this.factory.Create();

        await Assert.ThrowsAsync<NotFoundException>(
            () => client.DeletePipeAsync(
                new DeletePipeRequest { Name = $"flocilab-pipe-missing-{Guid.NewGuid():N}" },
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Pipes_Behind()
    {
        EventBridgePipesDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonPipes client = this.factory.Create();
        ListPipesResponse before = await client.ListPipesAsync(new ListPipesRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListPipesResponse after = await client.ListPipesAsync(new ListPipesRequest(), ct);

        // ?? [] rather than a bare dereference: AWSSDK v4 leaves an unset response collection
        // null, and floci only happens to send {"Pipes":[]} today. The day it omits the key on an
        // empty account this tripwire would NRE instead of asserting — the same nullable-shape
        // trap the EventBridge sample hit on FailedEntryCount.
        Assert.Equal(
            (before.Pipes ?? []).Select(p => p.Name).Order(),
            (after.Pipes ?? []).Select(p => p.Name).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render six red steps blaming the
    /// emulator for the user leaving.
    ///
    /// Cancelled mid-run rather than up front: a token that is already cancelled makes the very
    /// first SDK call throw, so no step is ever yielded and "no failed steps" holds vacuously. The
    /// assertion only has teeth once at least one step has been observed.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        EventBridgePipesDemo demo = new(this.factory);
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
        EventBridgePipesDemo demo = new(new EventBridgePipesClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
