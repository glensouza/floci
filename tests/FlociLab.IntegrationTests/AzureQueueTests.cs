using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.Queue;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-az per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
///
/// floci-az does not implement Queue Storage yet (docs/BLAZOR-PLAN.md §14): <c>CreateQueue</c> and
/// <c>DeleteQueue</c> answer a clean 501, but <c>ListQueues</c> silently serves the *blob*
/// container listing back with a 200, which the SDK's queue-list deserializer cannot parse and
/// throws on. Every test here pins that behaviour rather than skipping it, so the suite becomes
/// the tripwire for the day any of it lands upstream.
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

    /// <summary>
    /// The classification the coverage matrix shows today: not a clean 501, because the emulator
    /// answers 200 to ListQueues — the client throws deserializing a body shaped for Blob. That is
    /// an SDK-side failure, which is exactly what <see cref="ProbeStatus.Error"/> means.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Error()
    {
        ProbeResult result = await new QueueDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Error, result.Status);
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

    /// <summary>
    /// Every step in the round trip fails today, in a chain that follows directly from the
    /// listing quirk documented on this class: ListQueues throws client-side, CreateQueue answers
    /// a clean 501 so the queue never exists, and everything after that answers QueueNotFound —
    /// this is the honest wire behaviour, not a bug in the demo.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Documents_Queue_Storage_Is_Not_Yet_Implemented()
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
            s => Assert.Equal("SendMessage", s.Title),
            s => Assert.Equal("ReceiveMessage", s.Title),
            s => Assert.Equal("DeleteMessage", s.Title),
            s => Assert.Equal("DeleteQueue — cleanup", s.Title));

        Assert.All(steps, s => Assert.False(s.Succeeded, $"{s.Title} succeeded — floci-az may have shipped Queue Storage; update this test and docs/BLAZOR-PLAN.md §14."));
    }

    /// <summary>
    /// The half of the quirk that is a clean, documented outcome. Nothing in the demo calls this
    /// bare — RunAsync's CreateQueue step exercises the same path — but pinning it directly keeps
    /// the 501 legible as its own fact rather than only visible inside a failing round trip.
    /// </summary>
    [Fact]
    public async Task CreateQueue_Is_Not_Implemented()
    {
        QueueServiceClient client = this.factory.Create();

        RequestFailedException ex = await Assert.ThrowsAsync<RequestFailedException>(
            async () => await client.CreateQueueAsync("flocilab-probe-queue", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(501, ex.Status);
    }

    /// <summary>
    /// The half of the quirk that is not clean: the emulator answers 200 with a body shaped for
    /// Blob's container listing rather than Queue Storage's own shape, and the SDK throws
    /// deserializing it. This is the tripwire for the day ListQueues starts returning a real,
    /// parseable queue list — at that point <see cref="Probe_Reports_Error"/> above should also be
    /// revisited, since Probe would then most likely report Ok.
    /// </summary>
    [Fact]
    public async Task ListQueues_Throws_Because_The_Emulator_Serves_The_Blob_Container_List()
    {
        QueueServiceClient client = this.factory.Create();

        await Assert.ThrowsAsync<NullReferenceException>(async () =>
        {
            await foreach (QueueItem _ in
                client.GetQueuesAsync(cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false))
            {
            }
        });
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
