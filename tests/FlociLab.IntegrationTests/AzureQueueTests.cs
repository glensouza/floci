using QueueProperties = Azure.Storage.Queues.Models.QueueProperties;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.Queue;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-az per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
///
/// floci-az serves Queue Storage under <c>/{account}-queue</c>. Until 2026-09-28 this repo pointed
/// the queue endpoint at the bare account path — Blob's — and recorded the resulting 501s as
/// "floci-az does not implement Queue Storage" (docs/BLAZOR-PLAN.md §14). The last test below pins
/// what that wrong address does, so the misdiagnosis stays reproducible rather than anecdotal.
/// </summary>
public sealed class AzureQueueTests : IAsyncLifetime
{
    private const int FlociAzPort = 4577;

    // A plain ContainerBuilder rather than the FlociBuilder the S3 tests use — see AzureBlobTests
    // for why (Testcontainers.Floci hardcodes port 4566, floci-az listens on 4577).
    private readonly IContainer flociAz = new ContainerBuilder("floci/floci-az:latest")
        .WithPortBinding(FlociAzPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci/health").ForPort(FlociAzPort)))
        .Build();

    private QueueClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociAz.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new QueueClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociAz.DisposeAsync();

    private string Endpoint => $"http://{this.flociAz.Hostname}:{this.flociAz.GetMappedPublicPort(FlociAzPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new QueueDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        QueueDemo demo = new(new QueueClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new QueueDemo(this.factory));

        Assert.Collection(
            steps,
            s => Assert.Equal("ListQueues — before", s.Title),
            s => Assert.Equal("CreateQueue", s.Title),
            s => Assert.Equal("SendMessage", s.Title),
            s => Assert.Equal("ReceiveMessage", s.Title),
            s => Assert.Equal("DeleteMessage", s.Title),
            s => Assert.Equal("DeleteQueue — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // The message has to come back, not merely a 200 on the receive.
        Assert.Contains("Hello from FlociLab.", steps[3].Response);
    }

    /// <summary>
    /// Unique per-run queue names plus the unconditional cleanup make re-runs idempotent; a second
    /// run against the same container is how that is proved rather than asserted.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice_Without_Colliding()
    {
        QueueDemo demo = new(this.factory);

        foreach (int _ in Enumerable.Range(0, 2))
        {
            List<DemoStep> steps = await RunAsync(demo);

            Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        }
    }

    [Fact]
    public async Task Queue_Capability_RoundTrips()
    {
        QueueQueue queue = new(this.factory);
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

            // ReceiveMessagesAsync acks what it returns (interface contract). A second receive
            // alone cannot prove that — a received-but-undeleted message is merely invisible for
            // the visibility timeout — so the queue's own count, which includes hidden messages,
            // is the postcondition.
            QueueProperties properties = await this.factory.Create().GetQueueClient(name).GetPropertiesAsync(ct);

            Assert.Equal(0, properties.ApproximateMessagesCount);
        }
        finally
        {
            await queue.DeleteQueueAsync(name, CancellationToken.None);
        }

        Assert.DoesNotContain(name, (await queue.ListQueuesAsync(ct)).Select(q => q.Name));
    }

    /// <summary>
    /// The misdiagnosis, pinned. A queue request addressed to the bare account path — Blob's — is
    /// read as a blob PUT and answers a clean 501, which is exactly what this repo took for
    /// "Queue Storage is not implemented" through floci-az 0.13.0. Raw HTTP rather than the SDK,
    /// because the point is the address, not the client.
    /// </summary>
    [Fact]
    public async Task A_Queue_Request_On_The_Bare_Account_Path_Reaches_Blob_And_Answers_NotImplemented()
    {
        using HttpClient http = new();
        using HttpRequestMessage request = new(HttpMethod.Put, $"{this.Endpoint}/{new AzureEmulatorOptions().AccountName}/flocilab-misrouted-queue");
        request.Headers.Add("x-ms-version", "2025-01-05");

        using HttpResponseMessage response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(501, (int)response.StatusCode);
    }

    private static async Task<List<DemoStep>> RunAsync(QueueDemo demo)
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        return steps;
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
