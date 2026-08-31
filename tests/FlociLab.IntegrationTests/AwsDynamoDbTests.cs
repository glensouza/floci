using FlociLab.Aws.DynamoDb;
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
public sealed class AwsDynamoDbTests : IAsyncLifetime
{
    // Same reasoning as AwsS3Tests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private DynamoDbClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new DynamoDbClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new DynamoDbDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new DynamoDbDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListTables — before", s.Title),
            s => Assert.Equal("CreateTable", s.Title),
            s => Assert.Equal("PutItem", s.Title),
            s => Assert.Equal("GetItem", s.Title),
            s => Assert.Equal("DeleteItem", s.Title),
            s => Assert.Equal("DeleteTable — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "GetItem").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Tables_Behind()
    {
        DynamoDbDemo demo = new(this.factory);
        DynamoDbDocumentDb documentDb = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<CollectionInfo> before = await documentDb.ListCollectionsAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<CollectionInfo> after = await documentDb.ListCollectionsAsync(ct);

        Assert.Equal(before.Select(t => t.Name).Order(), after.Select(t => t.Name).Order());
    }

    /// <summary>The capability the document DB comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task DocumentDb_Capability_RoundTrips()
    {
        DynamoDbDocumentDb documentDb = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";
        string id = Guid.NewGuid().ToString("N");

        await documentDb.CreateCollectionAsync(name, ct);

        try
        {
            Assert.Contains(name, (await documentDb.ListCollectionsAsync(ct)).Select(t => t.Name));

            await documentDb.UpsertDocumentAsync(
                name, id, $$"""{"id":"{{id}}","greeting":"capability round-trip"}""", ct);

            string? document = await documentDb.GetDocumentAsync(name, id, ct);

            Assert.NotNull(document);
            Assert.Contains("capability round-trip", document);
        }
        finally
        {
            await documentDb.DeleteCollectionAsync(name, CancellationToken.None);
        }

        Assert.DoesNotContain(name, (await documentDb.ListCollectionsAsync(ct)).Select(t => t.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render six red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        DynamoDbDemo demo = new(this.factory);
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
        DynamoDbDemo demo = new(new DynamoDbClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
