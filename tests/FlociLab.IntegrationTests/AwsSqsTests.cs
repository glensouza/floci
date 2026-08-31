using FlociLab.Aws.Sqs;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
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
public sealed class AwsSqsTests : IAsyncLifetime
{
    // Same reasoning as AwsS3Tests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private SqsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new SqsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new SqsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SqsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListQueues — before", s.Title),
            s => Assert.Equal("CreateQueue", s.Title),
            s => Assert.Equal("SendMessage", s.Title),
            s => Assert.Equal("ReceiveMessage", s.Title),
            s => Assert.Equal("DeleteMessage", s.Title),
            s => Assert.Equal("DeleteQueue — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "ReceiveMessage").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Queues_Behind()
    {
        SqsDemo demo = new(this.factory);
        SqsQueue queue = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<QueueInfo> before = await queue.ListQueuesAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<QueueInfo> after = await queue.ListQueuesAsync(ct);

        Assert.Equal(before.Select(q => q.Name).Order(), after.Select(q => q.Name).Order());
    }

    /// <summary>The capability the queue comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task Queue_Capability_RoundTrips()
    {
        SqsQueue queue = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        await queue.CreateQueueAsync(name, ct);

        try
        {
            Assert.Contains(name, (await queue.ListQueuesAsync(ct)).Select(q => q.Name));

            await queue.SendMessageAsync(name, "capability round-trip", ct);

            IReadOnlyList<QueueMessage> received = await queue.ReceiveMessagesAsync(name, 1, ct);

            Assert.Single(received);
            Assert.Equal("capability round-trip", received[0].Body);

            // ReceiveMessagesAsync acks what it returns (interface contract), so a second receive
            // on the same queue finds nothing left.
            Assert.Empty(await queue.ReceiveMessagesAsync(name, 1, ct));
        }
        finally
        {
            await queue.DeleteQueueAsync(name, CancellationToken.None);
        }

        Assert.DoesNotContain(name, (await queue.ListQueuesAsync(ct)).Select(q => q.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render six red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        SqsDemo demo = new(this.factory);
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
        SqsDemo demo = new(new SqsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// SQS rejects a MaxNumberOfMessages above 10 with InvalidParameterValue, while Pub/Sub and
    /// Service Bus take larger batches. The capability clamps rather than forwarding, so a
    /// comparison page asking all four providers for 20 messages gets an answer from the SQS
    /// column instead of an exception. Unclamped, this test throws.
    /// </summary>
    [Fact]
    public async Task Queue_Capability_Clamps_Batch_Size_To_The_Sqs_Limit()
    {
        SqsQueue queue = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-clamp-{Guid.NewGuid():N}";

        await queue.CreateQueueAsync(name, ct);

        try
        {
            await queue.SendMessageAsync(name, "over the batch limit", ct);

            IReadOnlyList<QueueMessage> received = await queue.ReceiveMessagesAsync(name, 20, ct);

            Assert.Single(received);
            Assert.Equal("over the batch limit", received[0].Body);
        }
        finally
        {
            await queue.DeleteQueueAsync(name, CancellationToken.None);
        }
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
