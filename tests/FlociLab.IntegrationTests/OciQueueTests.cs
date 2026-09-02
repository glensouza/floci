using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Oci.Queue;
using Microsoft.Extensions.Options;
using Oci.QueueService;
using Oci.QueueService.Models;
using Oci.QueueService.Requests;
using Oci.QueueService.Responses;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-oci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class OciQueueTests : IAsyncLifetime
{
    private const int FlociOciPort = 4599;

    // A plain ContainerBuilder, not FlociBuilder — see OciObjectStorageTests for why: that type
    // hardcodes port 4566, and floci-oci listens on 4599 with a namespaced health path.
    private readonly IContainer flociOci = new ContainerBuilder("floci/floci-oci:latest")
        .WithPortBinding(FlociOciPort, assignRandomHostPort: true)
        // The tenancy OCID the lab uses everywhere. The image issues none of its own and verifies
        // nothing, but passing it keeps the container's idea of the tenancy and the sample's
        // compartment OCID the same value, which is what the AppHost does too.
        .WithEnvironment("FLOCI_OCI_DEFAULT_TENANCY_ID", OciEmulatorOptions.DefaultTenancyId)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-oci/health").ForPort(FlociOciPort)))
        .Build();

    private QueueClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociOci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new QueueClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociOci.DisposeAsync();

    // Hostname rather than "localhost" deliberately. Testcontainers hands back an address, and on
    // a Windows host "localhost" resolves to ::1 first while the published port is IPv4-only —
    // every first connection then eats a ~2 s dead IPv6 attempt before falling back.
    private string Endpoint => $"http://{this.flociOci.Hostname}:{this.flociOci.GetMappedPublicPort(FlociOciPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new QueueDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new QueueDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListQueues — before", s.Title),
            s => Assert.Equal("CreateQueue", s.Title),
            s => Assert.Equal("PutMessages", s.Title),
            s => Assert.Equal("GetMessages", s.Title),
            s => Assert.Equal("DeleteMessage", s.Title),
            s => Assert.Equal("DeleteQueue — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "GetMessages").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// CreateQueue and DeleteQueue are both asynchronous (an opc-work-request-id, exactly like real
    /// OCI), so this is also the tripwire for the waiter actually being awaited rather than the 202
    /// being trusted — a fire-and-forget delete would leave the second run's "before" list non-empty.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Queues_Behind()
    {
        QueueDemo demo = new(this.factory);
        OciQueue queue = new(this.factory);
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

    /// <summary>
    /// A run that cannot even build its admin client has to render the reason rather than take the
    /// page down with it. <c>QueueClientFactory.CreateAdmin()</c> refuses real-cloud mode with the
    /// lab's synthetic tenancy, and that refusal happens before the first request — so if the
    /// construction ever moves back outside <c>RunAsync</c>'s try, the iterator throws on the first
    /// <c>MoveNextAsync</c>, escapes the page's <c>OperationCanceledException</c>-only catch, and
    /// kills the Blazor circuit instead of showing a failed step.
    /// </summary>
    [Fact]
    public async Task Client_Construction_Failure_Becomes_A_Failed_Step()
    {
        QueueClientFactory refusing = new(new OciEndpoints(
            Options.Create(new FlociOptions { Oci = new OciEmulatorOptions { UseEmulator = false } })));

        List<DemoStep> steps = [];

        await foreach (DemoStep step in new QueueDemo(refusing).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        DemoStep only = Assert.Single(steps);

        Assert.False(only.Succeeded);
        Assert.Contains("TenancyId", only.Error);
    }

    /// <summary>
    /// Pins the emulator quirk the sample is built around (plan §14): floci-oci reports a queue's
    /// <c>messagesEndpoint</c> from its own <c>FLOCI_OCI_HOSTNAME</c> configuration — defaulting to
    /// the literal <c>localhost</c> — rather than from the address the caller reached, and it
    /// ignores the <c>Host</c> header. This container publishes a random host port, so the reported
    /// endpoint provably reaches nothing, which is why <c>QueueClientFactory.CreateData</c>
    /// overrides it with <c>ForFloci</c> instead of dialling it. If upstream ever starts echoing
    /// the request's host — the way floci-az does for Cosmos — this fails and the workaround can go.
    /// </summary>
    [Fact]
    public async Task MessagesEndpoint_Is_Reported_From_Config_Not_From_The_Request()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-endpoint-{Guid.NewGuid():N}";

        await new OciQueue(this.factory).CreateQueueAsync(name, ct);

        try
        {
            QueueAdminClient admin = this.factory.CreateAdmin();
            ListQueuesResponse listed = await admin.ListQueues(
                new ListQueuesRequest { CompartmentId = OciEmulatorOptions.DefaultTenancyId, DisplayName = name }, cancellationToken: ct);
            QueueSummary queue = listed.QueueCollection.Items.Single(q => q.DisplayName == name);

            Assert.Equal("http://localhost:4599", queue.MessagesEndpoint);

            // The address this test actually reached the emulator on. The reported endpoint is not
            // it — that is the whole point, and the reason the sample never dials what it is told.
            Assert.NotEqual(this.Endpoint, queue.MessagesEndpoint);
        }
        finally
        {
            await new OciQueue(this.factory).DeleteQueueAsync(name, CancellationToken.None);
        }
    }

    /// <summary>The capability the queue comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task Queue_Capability_RoundTrips()
    {
        OciQueue queue = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        await queue.CreateQueueAsync(name, ct);

        try
        {
            Assert.Contains(name, (await queue.ListQueuesAsync(ct)).Select(q => q.Name));

            await queue.SendMessageAsync(name, "capability round-trip", ct);

            IReadOnlyList<QueueMessage> received = await queue.ReceiveMessagesAsync(name, 1, ct);
            QueueMessage message = Assert.Single(received);

            Assert.Equal("capability round-trip", message.Body);
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
        QueueDemo demo = new(this.factory);
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
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is reserved
    /// and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        QueueDemo demo = new(new QueueClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static OciEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Oci = new OciEmulatorOptions { Endpoint = endpoint } }));
}
