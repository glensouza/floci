using Amazon.SSOAdmin;
using Amazon.SSOAdmin.Model;
using FlociLab.Aws.IdentityCenter;
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
public sealed class AwsIdentityCenterTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private IdentityCenterClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new IdentityCenterClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new IdentityCenterDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new IdentityCenterDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListInstances", s.Title),
            s => Assert.Equal("CreatePermissionSet", s.Title),
            s => Assert.Equal("CreatePermissionSet — refused as a duplicate", s.Title),
            s => Assert.Equal("AttachManagedPolicyToPermissionSet", s.Title),
            s => Assert.Equal("PutInlinePolicyToPermissionSet", s.Title),
            s => Assert.Equal("UpdatePermissionSet", s.Title),
            s => Assert.Equal("DescribePermissionSet — the update reads back", s.Title),
            s => Assert.Equal("ListManagedPoliciesInPermissionSet and GetInlinePolicyForPermissionSet", s.Title),
            s => Assert.Equal("TagResource", s.Title),
            s => Assert.Equal("ListTagsForResource", s.Title),
            s => Assert.Equal("ProvisionPermissionSet", s.Title),
            s => Assert.Equal("DeletePermissionSet", s.Title),
            s => Assert.Equal("DescribePermissionSet — refused once deleted", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        IdentityCenterDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonSSOAdmin client = this.factory.Create();
        string instanceArn = await InstanceArnAsync(client, ct);
        ListPermissionSetsResponse before = await client.ListPermissionSetsAsync(new ListPermissionSetsRequest { InstanceArn = instanceArn }, ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListPermissionSetsResponse after = await client.ListPermissionSetsAsync(new ListPermissionSetsRequest { InstanceArn = instanceArn }, ct);

        Assert.Equal((before.PermissionSets ?? []).Order(), (after.PermissionSets ?? []).Order());
    }

    /// <summary>
    /// The cleanup path on its own: a run stopped after the create — the consumer walking away, as
    /// the page does on dispose — still has a permission set, and cleanup has to find it by name.
    /// </summary>
    [Fact]
    public async Task Abandoned_Run_Is_Cleaned_Up_By_Name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonSSOAdmin client = this.factory.Create();
        string instanceArn = await InstanceArnAsync(client, ct);
        ListPermissionSetsResponse before = await client.ListPermissionSetsAsync(new ListPermissionSetsRequest { InstanceArn = instanceArn }, ct);

        List<DemoStep> steps = [];

        await foreach (DemoStep step in new IdentityCenterDemo(this.factory).RunAsync(ct))
        {
            steps.Add(step);

            if (step.Title == "AttachManagedPolicyToPermissionSet")
            {
                break;
            }
        }

        Assert.Equal("AttachManagedPolicyToPermissionSet", steps[^1].Title);

        ListPermissionSetsResponse after = await client.ListPermissionSetsAsync(new ListPermissionSetsRequest { InstanceArn = instanceArn }, ct);

        Assert.Equal((before.PermissionSets ?? []).Order(), (after.PermissionSets ?? []).Order());
    }

    /// <summary>
    /// floci ships exactly one Identity Center instance and answers the permission-set surface the
    /// sample uses; account assignments need an Identity Store user, a second package, so they are
    /// out of scope. The sample takes the first instance, so when this stops being a single
    /// <c>floci-identity-center</c> the assumption needs a look.
    /// </summary>
    [Fact]
    public async Task Emulator_Behaviours_The_Sample_Documents()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonSSOAdmin client = this.factory.Create();

        ListInstancesResponse instances = await client.ListInstancesAsync(new ListInstancesRequest(), ct);

        InstanceMetadata instance = Assert.Single(instances.Instances);
        Assert.Equal("floci-identity-center", instance.Name);
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        IdentityCenterDemo demo = new(this.factory);
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
        IdentityCenterDemo demo = new(new IdentityCenterClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// Without an instance there is nothing to run against, so the run stops at the one step that
    /// explains why rather than burying it under twelve more failures with empty ARNs.
    /// </summary>
    [Fact]
    public async Task Run_Stops_At_ListInstances_When_It_Fails()
    {
        IdentityCenterDemo demo = new(new IdentityCenterClientFactory(EndpointsFor("http://127.0.0.1:1")));
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        DemoStep only = Assert.Single(steps);
        Assert.Equal("ListInstances", only.Title);
        Assert.False(only.Succeeded);
    }

    private static async Task<string> InstanceArnAsync(IAmazonSSOAdmin client, CancellationToken ct)
    {
        ListInstancesResponse instances = await client.ListInstancesAsync(new ListInstancesRequest(), ct);

        return instances.Instances[0].InstanceArn;
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
