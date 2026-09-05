using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using FlociLab.Aws.Iam;
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
public sealed class AwsIamTests : IAsyncLifetime
{
    // Same reasoning as AwsKmsTests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private IamClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new IamClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new IamDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RunAsync_Every_Step_Succeeds_And_Ends_With_The_Resource_Tree()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new IamDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateUser", s.Title),
            s => Assert.Equal("CreateRole", s.Title),
            s => Assert.Equal("AttachRolePolicy", s.Title),
            s => Assert.Equal("ListAttachedRolePolicies", s.Title),
            s => Assert.Equal("Resource tree", s.Title),
            s => Assert.Equal("DetachRolePolicy — cleanup", s.Title),
            s => Assert.Equal("DeleteRole — cleanup", s.Title),
            s => Assert.Equal("DeleteUser — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        DemoStep tree = steps.Single(s => s.Title == "Resource tree");
        Assert.Null(tree.Request);
        Assert.Contains("ReadOnlyAccess", tree.Response);
    }

    /// <summary>
    /// A re-run must not find the previous run's role and user still attached to each other —
    /// cleanup detaches the policy and deletes both, so the account returns to what it was before,
    /// unlike KMS's ScheduleKeyDeletion.
    ///
    /// Running the round trip twice is necessary but nowhere near sufficient: every run picks a
    /// fresh GUID suffix, so the second run collides with nothing and goes green whether cleanup
    /// deleted everything or nothing at all. The assertions that make this a real test are the
    /// ones after the loop — the user and the role the last run created must both be gone.
    /// </summary>
    [Fact]
    public async Task RunAsync_Is_Idempotent_And_Leaves_Nothing_Behind()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<DemoStep> steps = [];

        for (int i = 0; i < 2; i++)
        {
            steps.Clear();

            await foreach (DemoStep step in new IamDemo(this.factory).RunAsync(ct))
            {
                steps.Add(step);
            }

            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {i}, {s.Title}: {s.Error}"));
        }

        string userName = NameFrom(steps, "CreateUser", "UserName");
        string roleName = NameFrom(steps, "CreateRole", "RoleName");

        using IAmazonIdentityManagementService client = this.factory.Create();

        await Assert.ThrowsAsync<NoSuchEntityException>(
            () => client.GetUserAsync(new GetUserRequest { UserName = userName }, ct));
        await Assert.ThrowsAsync<NoSuchEntityException>(
            () => client.GetRoleAsync(new GetRoleRequest { RoleName = roleName }, ct));
    }

    /// <summary>
    /// RunAsync generates its own resource names, so the step's own request line is the only place
    /// a test can learn which user and role that run actually created.
    /// </summary>
    private static string NameFrom(List<DemoStep> steps, string stepTitle, string parameterName)
    {
        string request = steps.Single(s => s.Title == stepTitle).Request!;
        string marker = $"{parameterName}=";
        int start = request.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int end = request.IndexOf('\n', start);

        return request[start..end];
    }

    /// <summary>
    /// Real IAM (and floci, verified 2026-09-04) refuses to delete a role while a policy is still
    /// attached to it — this is why RunAsync's cleanup detaches before it deletes.
    /// </summary>
    [Fact]
    public async Task DeleteRole_Fails_While_A_Policy_Is_Still_Attached()
    {
        using IAmazonIdentityManagementService client = this.factory.Create();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string roleName = $"flocilab-iam-conflict-{Guid.NewGuid():N}";
        const string trustPolicy = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},"Action":"sts:AssumeRole"}]}""";

        await client.CreateRoleAsync(new CreateRoleRequest { RoleName = roleName, AssumeRolePolicyDocument = trustPolicy }, ct);
        await client.AttachRolePolicyAsync(new AttachRolePolicyRequest { RoleName = roleName, PolicyArn = "arn:aws:iam::aws:policy/ReadOnlyAccess" }, ct);

        await Assert.ThrowsAsync<DeleteConflictException>(
            () => client.DeleteRoleAsync(new DeleteRoleRequest { RoleName = roleName }, ct));

        await client.DetachRolePolicyAsync(new DetachRolePolicyRequest { RoleName = roleName, PolicyArn = "arn:aws:iam::aws:policy/ReadOnlyAccess" }, ct);
        await client.DeleteRoleAsync(new DeleteRoleRequest { RoleName = roleName }, ct);
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        IamDemo demo = new(this.factory);
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
        IamDemo demo = new(new IamClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
