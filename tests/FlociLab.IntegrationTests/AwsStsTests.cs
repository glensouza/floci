using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using FlociLab.Aws.Sts;
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
public sealed class AwsStsTests : IAsyncLifetime
{
    // Same reasoning as AwsKmsTests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private StsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new StsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new StsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    /// <summary>
    /// Every step green, the assumed credentials resolving to the role, and — because the page can
    /// be pointed at real AWS — no live secret anywhere in what it renders.
    /// </summary>
    [Fact]
    public async Task RunAsync_Every_Step_Succeeds_And_Never_Renders_A_Secret()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new StsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("GetCallerIdentity", s.Title),
            s => Assert.Equal("AssumeRole", s.Title),
            s => Assert.Equal("GetCallerIdentity as the assumed role", s.Title),
            s => Assert.Equal("GetSessionToken", s.Title),
            s => Assert.Equal("GetFederationToken", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains(":assumed-role/flocilab-sts-role-", steps.Single(s => s.Title == "GetCallerIdentity as the assumed role").Response);

        List<DemoStep> withCredentials = steps.Where(s => s.Response?.Contains("AccessKeyId: ", StringComparison.Ordinal) == true).ToList();

        Assert.Equal(3, withCredentials.Count);
        Assert.All(withCredentials, s => Assert.Contains("SecretAccessKey: (withheld)", s.Response));
        Assert.All(withCredentials, s => Assert.Contains("characters, withheld)", s.Response));

        // The markers alone would still pass if a secret leaked somewhere else in the text. Secret
        // keys are 40 characters of this alphabet and session tokens far longer (floci matches
        // real AWS here). The one other run that long is the per-run role name joined by '/' to
        // the session name in the assumed-role ARN, so that is masked first.
        Assert.All(steps, s => Assert.DoesNotMatch(
            "[A-Za-z0-9/+=]{40,}",
            Regex.Replace($"{s.Request}\n{s.Response}", "flocilab-sts-role-[0-9a-f]{32}", "<role>")));
    }

    /// <summary>
    /// A run leaves no state behind, so a second run has to behave like the first — no
    /// "already exists", no expired-token carry-over.
    /// </summary>
    [Fact]
    public async Task RunAsync_Is_Idempotent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        for (int i = 0; i < 2; i++)
        {
            List<DemoStep> steps = [];

            await foreach (DemoStep step in new StsDemo(this.factory).RunAsync(ct))
            {
                steps.Add(step);
            }

            Assert.Equal(5, steps.Count);
            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {i}, {s.Title}: {s.Error}"));
        }
    }

    /// <summary>
    /// Tripwire, not a sample assertion: floci 2.1.0 hands credentials to a role that was never
    /// created, where real STS answers AccessDenied (docs/BLAZOR-PLAN.md §14). When this starts
    /// failing, floci began validating the role — and the demo's AssumeRole step now needs a real
    /// role created first.
    /// </summary>
    [Fact]
    public async Task AssumeRole_Accepts_A_Role_That_Was_Never_Created()
    {
        using IAmazonSecurityTokenService client = this.factory.Create();

        AssumeRoleResponse response = await client.AssumeRoleAsync(
            new AssumeRoleRequest { RoleArn = "arn:aws:iam::000000000000:role/never-created", RoleSessionName = "tripwire" },
            TestContext.Current.CancellationToken);

        Assert.Contains("assumed-role/never-created/", response.AssumedRoleUser.Arn);
    }

    /// <summary>
    /// Tripwire: floci 2.1.0 accepts a <c>DurationSeconds</c> outside real STS's 900–43200 and
    /// names every assumed-role session <c>floci-session</c> whatever was asked for
    /// (docs/BLAZOR-PLAN.md §14). The demo's identity check matches on the role, deliberately not
    /// the session name, because of the second. When this fails, revisit both.
    /// </summary>
    [Fact]
    public async Task AssumeRole_Ignores_Duration_Bounds_And_The_Session_Name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonSecurityTokenService client = this.factory.Create();

        // The SDK's analyzer rejects a literal below 900, which is the point: this test exists to
        // send one and watch floci accept it.
#pragma warning disable SecurityTokenService1003
        AssumeRoleResponse response = await client.AssumeRoleAsync(
            new AssumeRoleRequest { RoleArn = "arn:aws:iam::000000000000:role/tripwire", RoleSessionName = "asked-for", DurationSeconds = 1 },
            ct);
#pragma warning restore SecurityTokenService1003

        using IAmazonSecurityTokenService assumedClient = this.factory.Create(
            new SessionAWSCredentials(response.Credentials.AccessKeyId, response.Credentials.SecretAccessKey, response.Credentials.SessionToken));
        GetCallerIdentityResponse identity = await assumedClient.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);

        Assert.EndsWith(":assumed-role/tripwire/floci-session", identity.Arn);
    }

    /// <summary>
    /// Tripwire: floci answers GetAccessKeyInfo with UnsupportedOperation. When upstream ships it,
    /// this fails and the demo can grow a step.
    /// </summary>
    [Fact]
    public async Task GetAccessKeyInfo_Is_Not_Supported()
    {
        using IAmazonSecurityTokenService client = this.factory.Create();

        AmazonServiceException ex = await Assert.ThrowsAnyAsync<AmazonServiceException>(
            () => client.GetAccessKeyInfoAsync(new GetAccessKeyInfoRequest { AccessKeyId = "AKIAIOSFODNN7EXAMPLE" }, TestContext.Current.CancellationToken));

        Assert.Equal("UnsupportedOperation", ex.ErrorCode);
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        StsDemo demo = new(this.factory);
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
        StsDemo demo = new(new StsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
