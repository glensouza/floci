using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.CosmosDb;
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
/// </summary>
public sealed class AzureCosmosDbTests : IAsyncLifetime
{
    private const int FlociAzPort = 4577;

    // A plain ContainerBuilder rather than the FlociBuilder the S3 tests use — see AzureBlobTests
    // for why (Testcontainers.Floci hardcodes port 4566, floci-az listens on 4577).
    private readonly IContainer flociAz = new ContainerBuilder("floci/floci-az:latest")
        .WithPortBinding(FlociAzPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci/health").ForPort(FlociAzPort)))
        .Build();

    private CosmosDbClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociAz.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CosmosDbClientFactory(EndpointsFor(this.Endpoint));
    }

    // Unlike QueueClientFactory, this factory owns a CosmosClient — an HTTP handler plus background
    // tasks — so the copied test shape is not enough: it has to be disposed with the container.
    public async ValueTask DisposeAsync()
    {
        this.factory?.Dispose();
        await this.flociAz.DisposeAsync();
    }

    private string Endpoint => $"http://{this.flociAz.Hostname}:{this.flociAz.GetMappedPublicPort(FlociAzPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CosmosDbDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new CosmosDbDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("EnsureDatabase", s.Title),
            s => Assert.Equal("ListContainers — before", s.Title),
            s => Assert.Equal("CreateContainer", s.Title),
            s => Assert.Equal("UpsertItem", s.Title),
            s => Assert.Equal("ReadItem", s.Title),
            s => Assert.Equal("DeleteItem", s.Title),
            s => Assert.Equal("DeleteContainer — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "ReadItem").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// The fixed "flocilab" database itself is expected to persist across runs — only the
    /// container, this run's own resource, has to disappear.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Containers_Behind()
    {
        CosmosDbDemo demo = new(this.factory);
        CosmosDbDocumentDb store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<CollectionInfo> before = await store.ListCollectionsAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<CollectionInfo> after = await store.ListCollectionsAsync(ct);

        Assert.Equal(before.Select(c => c.Name).Order(), after.Select(c => c.Name).Order());
    }

    /// <summary>The capability the document-DB comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task DocumentDb_Capability_RoundTrips()
    {
        CosmosDbDocumentDb store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string collection = $"flocilab-cap-{Guid.NewGuid():N}";
        string id = Guid.NewGuid().ToString("N");

        await store.CreateCollectionAsync(collection, ct);

        try
        {
            Assert.Contains(collection, (await store.ListCollectionsAsync(ct)).Select(c => c.Name));

            await store.UpsertDocumentAsync(collection, id, """{"greeting":"capability round-trip"}""", ct);

            string? fetched = await store.GetDocumentAsync(collection, id, ct);

            Assert.NotNull(fetched);
            Assert.Contains("capability round-trip", fetched);
        }
        finally
        {
            await store.DeleteCollectionAsync(collection, CancellationToken.None);
        }

        Assert.DoesNotContain(collection, (await store.ListCollectionsAsync(ct)).Select(c => c.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        CosmosDbDemo demo = new(this.factory);
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
        using CosmosDbClientFactory unreachable = new(EndpointsFor("http://127.0.0.1:1"));
        CosmosDbDemo demo = new(unreachable);

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
