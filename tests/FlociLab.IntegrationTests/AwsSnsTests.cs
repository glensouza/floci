using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FlociLab.Aws.Sns;
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
public sealed class AwsSnsTests : IAsyncLifetime
{
    // Same reasoning as AwsSqsTests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private SnsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new SnsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new SnsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SnsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListTopics — before", s.Title),
            s => Assert.Equal("CreateTopic", s.Title),
            s => Assert.Equal("Subscribe", s.Title),
            s => Assert.Equal("GetTopicAttributes", s.Title),
            s => Assert.Equal("Publish", s.Title),
            s => Assert.Equal("Unsubscribe", s.Title),
            s => Assert.Equal("DeleteTopic — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("SubscriptionsPending: 1", steps.Single(s => s.Title == "GetTopicAttributes").Response);
        Assert.Contains("MessageId:", steps.Single(s => s.Title == "Publish").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Topics_Behind()
    {
        SnsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonSimpleNotificationService client = this.factory.Create();
        IReadOnlyList<string> before = await ListTopicArnsAsync(client, ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<string> after = await ListTopicArnsAsync(client, ct);

        Assert.Equal(before.Order(), after.Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        SnsDemo demo = new(this.factory);
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
        SnsDemo demo = new(new SnsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// The request pane is a claim about the wire, and this sample was shaped after the SQS one,
    /// where that claim is <c>X-Amz-Target: AmazonSQS.&lt;Op&gt;</c>. SNS is query-protocol — form
    /// -urlencoded <c>Action=</c> in, XML out — so carrying the header over would put a request on
    /// camera that was never sent. Pins the format so the next copy-paste fails here (plan §14).
    /// </summary>
    [Fact]
    public async Task Request_Panes_Show_The_Query_Protocol_Not_A_Json_Target()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SnsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.All(steps, s =>
        {
            Assert.NotNull(s.Request);
            Assert.DoesNotContain("X-Amz-Target", s.Request);
            Assert.Contains("Action=", s.Request);
            Assert.Contains("Version=2010-03-31", s.Request);
        });
    }

    /// <summary>
    /// A divergence tripwire, not a behaviour we want (plan §14). Real SNS answers the literal
    /// string "pending confirmation" for an unconfirmed subscription unless
    /// <c>ReturnSubscriptionArn</c> is set; floci returns a usable ARN regardless, which is why the
    /// round-trip above passes without it and why the demo sets it anyway. If this ever fails,
    /// upstream has tightened SNS to match the cloud and the plan row can be retired.
    /// </summary>
    [Fact]
    public async Task Floci_Returns_A_Real_Arn_For_A_Pending_Subscription_Where_Aws_Would_Not()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonSimpleNotificationService client = this.factory.Create();

        CreateTopicResponse topic = await client.CreateTopicAsync(
            new CreateTopicRequest { Name = $"flocilab-sns-divergence-{Guid.NewGuid():N}" }, ct);

        try
        {
            // Deliberately without ReturnSubscriptionArn — that is the whole point of the check.
            SubscribeResponse subscription = await client.SubscribeAsync(
                new SubscribeRequest { TopicArn = topic.TopicArn, Protocol = "email", Endpoint = "nobody@flocilab.example" }, ct);

            Assert.NotEqual("pending confirmation", subscription.SubscriptionArn);
            Assert.StartsWith("arn:aws:sns:", subscription.SubscriptionArn);
        }
        finally
        {
            await client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic.TopicArn }, CancellationToken.None);
        }
    }

    private static async Task<IReadOnlyList<string>> ListTopicArnsAsync(IAmazonSimpleNotificationService client, CancellationToken ct)
    {
        ListTopicsResponse response = await client.ListTopicsAsync(new ListTopicsRequest(), ct).ConfigureAwait(false);

        return [.. (response.Topics ?? []).Select(t => t.TopicArn)];
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
