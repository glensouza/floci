using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using FlociLab.Aws.CloudWatchLogs;
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
public sealed class AwsCloudWatchLogsTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private CloudWatchLogsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CloudWatchLogsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CloudWatchLogsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new CloudWatchLogsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateLogGroup", s.Title),
            s => Assert.Equal("CreateLogStream", s.Title),
            s => Assert.Equal("PutLogEvents", s.Title),
            s => Assert.Equal("GetLogEvents", s.Title),
            s => Assert.Equal("DeleteLogGroup — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // "including this run's message" is the postcondition, not the call merely succeeding:
        // the step throws unless the read-back actually contained the message this run wrote.
        Assert.Contains("including this run's message", steps.Single(s => s.Title == "GetLogEvents").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. DeleteLogGroup removes the group and every stream and event
    /// inside it, so a re-run always starts from a clean account.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Log_Groups_Behind()
    {
        CloudWatchLogsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonCloudWatchLogs client = this.factory.Create();
        DescribeLogGroupsResponse before = await client.DescribeLogGroupsAsync(new DescribeLogGroupsRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        DescribeLogGroupsResponse after = await client.DescribeLogGroupsAsync(new DescribeLogGroupsRequest(), ct);

        Assert.Equal(
            before.LogGroups.Select(g => g.LogGroupName).Order(),
            after.LogGroups.Select(g => g.LogGroupName).Order());
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
        CloudWatchLogsDemo demo = new(this.factory);
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
    /// A cancelled run still deletes its log group — the live case, since the page cancels its own
    /// token on Dispose whenever a viewer navigates away mid-run.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Still_Deletes_The_Log_Group()
    {
        CloudWatchLogsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonCloudWatchLogs client = this.factory.Create();
        DescribeLogGroupsResponse before = await client.DescribeLogGroupsAsync(new DescribeLogGroupsRequest(), ct);

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                // Cancel the moment the create is on the wire, which is the tightest window
                // cleanup has to cover.
                await cts.CancelAsync();
            }
        });

        DescribeLogGroupsResponse after = await client.DescribeLogGroupsAsync(new DescribeLogGroupsRequest(), ct);

        Assert.Equal(
            before.LogGroups.Select(g => g.LogGroupName).Order(),
            after.LogGroups.Select(g => g.LogGroupName).Order());
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        CloudWatchLogsDemo demo = new(new CloudWatchLogsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// A failed run still reports its cleanup. This is the regression test for the `yield break`
    /// trap: `yield break` runs the finally — so the delete really does go out — but then
    /// terminates the iterator, dropping the cleanup step the finally had just computed. The page
    /// would show one red CreateLogGroup and never mention the delete it had issued. Nothing was
    /// listening on port 1, so every step fails; what is asserted is that the cleanup step is
    /// *yielded at all*, not that it succeeded.
    /// </summary>
    [Fact]
    public async Task Failed_Run_Still_Yields_Its_Cleanup_Step()
    {
        CloudWatchLogsDemo demo = new(new CloudWatchLogsClientFactory(EndpointsFor("http://127.0.0.1:1")));
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateLogGroup", s.Title),
            s => Assert.Equal("DeleteLogGroup — cleanup", s.Title));

        Assert.All(steps, s => Assert.False(s.Succeeded));
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
