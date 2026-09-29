using FlociLab.Aws.AppSync;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.Floci;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator the
/// AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class AwsAppSyncTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    // The real IHttpClientFactory the demo runs under in a host, from a minimal service provider.
    private readonly ServiceProvider httpServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();

    private AppSyncClientFactory factory = null!;

    private IHttpClientFactory HttpClientFactory => this.httpServices.GetRequiredService<IHttpClientFactory>();

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new AppSyncClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync()
    {
        await this.floci.DisposeAsync();
        await this.httpServices.DisposeAsync();
    }

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new AppSyncDemo(this.factory, this.HttpClientFactory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new AppSyncDemo(this.factory, this.HttpClientFactory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateGraphqlApi", s.Title),
            s => Assert.Equal("CreateApiKey", s.Title),
            s => Assert.Equal("StartSchemaCreation", s.Title),
            s => Assert.Equal("CreateDataSource (NONE)", s.Title),
            s => Assert.Equal("CreateResolver (Query.echo)", s.Title),
            s => Assert.Equal("Query with the API key", s.Title),
            s => Assert.Equal("Query without a key (expect 401)", s.Title),
            s => Assert.Equal("DeleteGraphqlApi — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Tripwire. floci 2.1.0 executes the query and enforces the key, but a field with a resolver
    /// comes back null (upstream's docs describe resolver execution on main, the released image
    /// does not do it yet). When this starts failing, resolvers landed — flip the assertion to the
    /// echoed string and update the §13 note.
    /// </summary>
    [Fact]
    public async Task Query_Resolver_Returns_Null_Until_Upstream_Executes_Resolvers()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new AppSyncDemo(this.factory, this.HttpClientFactory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        DemoStep query = steps.Single(s => s.Title == "Query with the API key");

        Assert.Contains("\"echo\":null", query.Response);
        Assert.Contains("resolver returned null", query.Response);
    }

    /// <summary>
    /// Re-runnable because every run creates a uniquely named GraphQL API and removes it in cleanup.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice_Without_Colliding()
    {
        AppSyncDemo demo = new(this.factory, this.HttpClientFactory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. Cancelled mid-run rather than
    /// up front: a token that is already cancelled makes the first SDK call throw, so no step is
    /// ever yielded and "no failed steps" would hold vacuously.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        AppSyncDemo demo = new(this.factory, this.HttpClientFactory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);

                await cts.CancelAsync();
            }
        });

        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Nothing listening has to read as Unreachable, not Error, or a stopped emulator looks like a
    /// broken sample. Port 1 is reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        AppSyncDemo demo = new(new AppSyncClientFactory(EndpointsFor("http://127.0.0.1:1")), this.HttpClientFactory);

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// A run that fails at the first step yields exactly that step plus the cleanup the finally
    /// always produces. With no id back, cleanup finds the API by name — and against a stopped
    /// emulator that lookup is honestly red too.
    /// </summary>
    [Fact]
    public async Task Failed_Run_Yields_Only_The_Steps_It_Reached()
    {
        AppSyncDemo demo = new(new AppSyncClientFactory(EndpointsFor("http://127.0.0.1:1")), this.HttpClientFactory);
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateGraphqlApi", s.Title),
            s => Assert.Equal("DeleteGraphqlApi — cleanup", s.Title));
        Assert.False(steps[0].Succeeded);
        Assert.False(steps[1].Succeeded, "cleanup claimed success against an emulator it could not reach");
        Assert.Contains("ListGraphqlApisAsync", steps[1].Request);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
