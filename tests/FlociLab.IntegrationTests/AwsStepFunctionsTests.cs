using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using FlociLab.Aws.StepFunctions;
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
public sealed class AwsStepFunctionsTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private StepFunctionsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new StepFunctionsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new StepFunctionsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new StepFunctionsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateStateMachine", s.Title),
            s => Assert.Equal("DescribeStateMachine", s.Title),
            s => Assert.Equal("StartExecution", s.Title),
            s => Assert.Equal("DescribeExecution", s.Title),
            s => Assert.Equal("ListExecutions", s.Title),
            s => Assert.Equal("DeleteStateMachine — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // floci actually executes the single Pass state, so the round trip is only proven if the
        // execution reached SUCCEEDED with the state's own literal result — not merely that
        // DescribeExecution answered 200.
        Assert.Contains("Status: SUCCEEDED, Output: \"ok\"", steps.Single(s => s.Title == "DescribeExecution").Response);

        // "including this run's" is the postcondition, not the count: the step throws unless the
        // listing actually contained the execution this run just started.
        Assert.Contains("including this run's", steps.Single(s => s.Title == "ListExecutions").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_StateMachines_Behind()
    {
        StepFunctionsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonStepFunctions client = this.factory.Create();
        ListStateMachinesResponse before = await client.ListStateMachinesAsync(new ListStateMachinesRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListStateMachinesResponse after = await client.ListStateMachinesAsync(new ListStateMachinesRequest(), ct);

        Assert.Equal(
            (before.StateMachines ?? []).Select(s => s.Name).Order(),
            (after.StateMachines ?? []).Select(s => s.Name).Order());
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
        StepFunctionsDemo demo = new(this.factory);
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
    /// A cancelled run still tears its state machine down — the live case, since the page cancels
    /// its own token on Dispose whenever a viewer navigates away mid-run.
    ///
    /// Worth being precise about what this does *not* cover: it cancels once the create step has
    /// already yielded, so the ARN is in hand and cleanup would run under the response-gated
    /// version this sample shipped with too. The window review actually found — the create lands
    /// but its response is lost, leaving cleanup with no ARN — is not reachable deterministically
    /// through the SDK, so `ResolveByNameAsync` rests on the §14 rule rather than on a tripwire
    /// here. This test pins the half that is reachable.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Still_Removes_The_State_Machine()
    {
        StepFunctionsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonStepFunctions client = this.factory.Create();
        ListStateMachinesResponse before = await client.ListStateMachinesAsync(new ListStateMachinesRequest(), ct);

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                // Cancel the moment the create is on the wire, which is the window the old
                // response-gated cleanup left open.
                await cts.CancelAsync();
            }
        });

        ListStateMachinesResponse after = await client.ListStateMachinesAsync(new ListStateMachinesRequest(), ct);

        Assert.Equal(
            (before.StateMachines ?? []).Select(s => s.Name).Order(),
            (after.StateMachines ?? []).Select(s => s.Name).Order());
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        StepFunctionsDemo demo = new(new StepFunctionsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
