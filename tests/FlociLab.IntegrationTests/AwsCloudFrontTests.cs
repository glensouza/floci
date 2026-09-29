using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using FlociLab.Aws.CloudFront;
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
public sealed class AwsCloudFrontTests : IAsyncLifetime
{
    // Same reasoning as AwsRoute53Tests: pinned to :latest so the tripwire tracks the same build
    // the AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private CloudFrontClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CloudFrontClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CloudFrontDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new CloudFrontDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListDistributions — before", s.Title),
            s => Assert.Equal("CreateOriginAccessControl", s.Title),
            s => Assert.Equal("CreateDistribution", s.Title),
            s => Assert.Equal("GetDistribution", s.Title),
            s => Assert.Equal("UpdateDistribution", s.Title),
            s => Assert.Equal("UpdateDistribution — stale ETag", s.Title),
            s => Assert.Equal("CreateInvalidation", s.Title),
            s => Assert.Equal("ListInvalidations", s.Title),
            s => Assert.Equal("TagResource", s.Title),
            s => Assert.Equal("ListTagsForResource", s.Title),
            s => Assert.Equal("DeleteDistribution — while enabled", s.Title),
            s => Assert.Equal("UpdateDistribution — disable", s.Title),
            s => Assert.Equal("DeleteDistribution", s.Title),
            s => Assert.Equal("DeleteOriginAccessControl", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("refused", steps.Single(s => s.Title == "UpdateDistribution — stale ETag").Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("DistributionNotDisabled", steps.Single(s => s.Title == "DeleteDistribution — while enabled").Response ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        CloudFrontDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonCloudFront client = this.factory.Create();
        ListDistributionsResponse distributionsBefore = await client.ListDistributionsAsync(new ListDistributionsRequest(), ct);
        ListOriginAccessControlsResponse controlsBefore = await client.ListOriginAccessControlsAsync(new ListOriginAccessControlsRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListDistributionsResponse distributionsAfter = await client.ListDistributionsAsync(new ListDistributionsRequest(), ct);
        ListOriginAccessControlsResponse controlsAfter = await client.ListOriginAccessControlsAsync(new ListOriginAccessControlsRequest(), ct);

        Assert.Equal((distributionsBefore.DistributionList?.Items ?? []).Select(d => d.Id).Order(), (distributionsAfter.DistributionList?.Items ?? []).Select(d => d.Id).Order());
        Assert.Equal((controlsBefore.OriginAccessControlList?.Items ?? []).Select(c => c.Id).Order(), (controlsAfter.OriginAccessControlList?.Items ?? []).Select(c => c.Id).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        CloudFrontDemo demo = new(this.factory);
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
        CloudFrontDemo demo = new(new CloudFrontClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// Tripwire (plan §14). CallerReference is CloudFront's idempotency token: real AWS answers
    /// <c>DistributionAlreadyExists</c> when it is replayed with a different configuration. floci
    /// 2.1.0 mints a second distribution instead. When this starts failing, upstream enforces it:
    /// add a replayed-CallerReference step to the sample, refusal as the passing outcome.
    /// </summary>
    [Fact]
    public async Task Tripwire_Replayed_CallerReference_Creates_A_Second_Distribution_On_Floci()
    {
        using IAmazonCloudFront client = this.factory.Create();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string run = Guid.NewGuid().ToString("N")[..12];
        List<string> created = [];

        try
        {
            foreach (string comment in new[] { "first", "different" })
            {
                CreateDistributionResponse response = await client.CreateDistributionAsync(
                    new CreateDistributionRequest
                    {
                        DistributionConfig = new DistributionConfig
                        {
                            CallerReference = run,
                            Comment = comment,
                            Enabled = false,
                            Origins = new Origins { Quantity = 1, Items = [new Origin { Id = "o", DomainName = $"{run}.s3.amazonaws.com", S3OriginConfig = new S3OriginConfig { OriginAccessIdentity = string.Empty } }] },
                            DefaultCacheBehavior = new DefaultCacheBehavior { TargetOriginId = "o", ViewerProtocolPolicy = ViewerProtocolPolicy.AllowAll, CachePolicyId = "658327ea-f89d-4fab-a63d-7e88639e58f6" },
                        },
                    }, ct);
                created.Add(response.Distribution.Id);
            }

            Assert.Equal(2, created.Distinct().Count());
        }
        finally
        {
            foreach (string id in created)
            {
                GetDistributionResponse current = await client.GetDistributionAsync(new GetDistributionRequest { Id = id }, CancellationToken.None);
                await client.DeleteDistributionAsync(new DeleteDistributionRequest { Id = id, IfMatch = current.ETag }, CancellationToken.None);
            }
        }
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
