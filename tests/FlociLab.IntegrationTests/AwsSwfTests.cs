using Amazon.SimpleWorkflow;
using Amazon.SimpleWorkflow.Model;
using FlociLab.Aws.Swf;
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
public sealed class AwsSwfTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private SwfClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new SwfClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new SwfDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SwfDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("RegisterDomain", s.Title),
            s => Assert.Equal("RegisterWorkflowType", s.Title),
            s => Assert.Equal("StartWorkflowExecution", s.Title),
            s => Assert.Equal("PollForDecisionTask", s.Title),
            s => Assert.Equal("RespondDecisionTaskCompleted", s.Title),
            s => Assert.Equal("DescribeWorkflowExecution", s.Title),
            s => Assert.Equal("ListClosedWorkflowExecutions", s.Title),
            s => Assert.Equal("DeprecateDomain — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // floci actually closes the execution once the decision is submitted, so the round trip
        // is only proven if it reached CLOSED/COMPLETED — not merely that the decision was
        // accepted.
        Assert.Contains(
            "ExecutionStatus: CLOSED, CloseStatus: COMPLETED",
            steps.Single(s => s.Title == "DescribeWorkflowExecution").Response);

        // "including this run's" is the postcondition, not the count: the step throws unless the
        // listing actually contained the execution this run just closed.
        Assert.Contains("including this run's", steps.Single(s => s.Title == "ListClosedWorkflowExecutions").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. Unlike Step Functions, SWF has no delete — deprecating a domain
    /// removes it from the REGISTERED listing permanently, which is the observable "gone" this
    /// test checks for.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Domains_Registered_Behind()
    {
        SwfDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonSimpleWorkflow client = this.factory.Create();
        ListDomainsResponse before = await client.ListDomainsAsync(
            new ListDomainsRequest { RegistrationStatus = RegistrationStatus.REGISTERED }, ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListDomainsResponse after = await client.ListDomainsAsync(
            new ListDomainsRequest { RegistrationStatus = RegistrationStatus.REGISTERED }, ct);

        Assert.Equal(
            (before.DomainInfos.Infos ?? []).Select(d => d.Name).Order(),
            (after.DomainInfos.Infos ?? []).Select(d => d.Name).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render eight red steps blaming
    /// the emulator for the user leaving.
    ///
    /// Cancelled mid-run rather than up front: a token that is already cancelled makes the very
    /// first SDK call throw, so no step is ever yielded and "no failed steps" holds vacuously. The
    /// assertion only has teeth once at least one step has been observed.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        SwfDemo demo = new(this.factory);
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
    /// A cancelled run still deprecates its domain — the live case, since the page cancels its own
    /// token on Dispose whenever a viewer navigates away mid-run.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Still_Deprecates_The_Domain()
    {
        SwfDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonSimpleWorkflow client = this.factory.Create();
        ListDomainsResponse before = await client.ListDomainsAsync(
            new ListDomainsRequest { RegistrationStatus = RegistrationStatus.REGISTERED }, ct);

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                // Cancel the moment the register is on the wire, which is the tightest window
                // cleanup has to cover.
                await cts.CancelAsync();
            }
        });

        ListDomainsResponse after = await client.ListDomainsAsync(
            new ListDomainsRequest { RegistrationStatus = RegistrationStatus.REGISTERED }, ct);

        Assert.Equal(
            (before.DomainInfos.Infos ?? []).Select(d => d.Name).Order(),
            (after.DomainInfos.Infos ?? []).Select(d => d.Name).Order());
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        SwfDemo demo = new(new SwfClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
