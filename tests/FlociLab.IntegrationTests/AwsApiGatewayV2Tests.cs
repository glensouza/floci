using FlociLab.Aws.ApiGatewayV2;
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
public sealed class AwsApiGatewayV2Tests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    // DefaultHttpClientFactory is internal to Microsoft.Extensions.Http, so the real
    // IHttpClientFactory implementation the demo will actually run under in a host comes from a
    // minimal service provider rather than a hand-rolled stub.
    private readonly ServiceProvider httpServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();

    private ApiGatewayV2ClientFactory factory = null!;

    private IHttpClientFactory HttpClientFactory => this.httpServices.GetRequiredService<IHttpClientFactory>();

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new ApiGatewayV2ClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync()
    {
        await this.floci.DisposeAsync();
        await this.httpServices.DisposeAsync();
    }

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new ApiGatewayV2Demo(this.factory, this.HttpClientFactory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new ApiGatewayV2Demo(this.factory, this.HttpClientFactory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateApi", s.Title),
            s => Assert.Equal("CreateIntegration (HTTP_PROXY)", s.Title),
            s => Assert.Equal("CreateRoute", s.Title),
            s => Assert.Equal("CreateStage (auto-deploy)", s.Title),
            s => Assert.Equal("Invoke deployed stage", s.Title),
            s => Assert.Equal("DeleteApi — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // The postcondition, not the call merely succeeding: the invoke step throws unless the
        // response body actually contains the upstream text the proxy was pointed at.
        Assert.Contains("proxied", steps.Single(s => s.Title == "Invoke deployed stage").Response);
    }

    /// <summary>
    /// Re-runnable because every run creates a uniquely named HTTP API and removes it in cleanup.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice_Without_Colliding()
    {
        ApiGatewayV2Demo demo = new(this.factory, this.HttpClientFactory);
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
        ApiGatewayV2Demo demo = new(this.factory, this.HttpClientFactory);
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
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        ApiGatewayV2Demo demo = new(new ApiGatewayV2ClientFactory(EndpointsFor("http://127.0.0.1:1")), this.HttpClientFactory);

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// A run that fails at the first step still yields exactly that one failed step, plus the
    /// cleanup step the finally always produces. With no id back from CreateRestApi, cleanup falls
    /// back to finding the API by name — and against a stopped emulator that lookup is honestly red
    /// too, rather than claiming there was nothing to remove when it never managed to look.
    /// </summary>
    [Fact]
    public async Task Failed_Run_Yields_Only_The_Steps_It_Reached()
    {
        ApiGatewayV2Demo demo = new(new ApiGatewayV2ClientFactory(EndpointsFor("http://127.0.0.1:1")), this.HttpClientFactory);
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateApi", s.Title),
            s => Assert.Equal("DeleteApi — cleanup", s.Title));
        Assert.False(steps[0].Succeeded);
        Assert.False(steps[1].Succeeded, "cleanup claimed success against an emulator it could not reach");
        Assert.Contains("GetApisAsync", steps[1].Request);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
