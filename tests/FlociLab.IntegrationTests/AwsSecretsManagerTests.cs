using FlociLab.Aws.SecretsManager;
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
public sealed class AwsSecretsManagerTests : IAsyncLifetime
{
    // Same reasoning as AwsS3Tests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private SecretsManagerClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new SecretsManagerClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new SecretsManagerDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SecretsManagerDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListSecrets — before", s.Title),
            s => Assert.Equal("CreateSecret", s.Title),
            s => Assert.Equal("GetSecretValue", s.Title),
            s => Assert.Equal("PutSecretValue", s.Title),
            s => Assert.Equal("GetSecretValue — after update", s.Title),
            s => Assert.Equal("DeleteSecret — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "GetSecretValue").Response);
        Assert.Contains("Updated from FlociLab.", steps.Single(s => s.Title == "GetSecretValue — after update").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. Unlike KMS's ScheduleKeyDeletion, DeleteSecret uses
    /// ForceDeleteWithoutRecovery so this actually holds.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Secrets_Behind()
    {
        SecretsManagerDemo demo = new(this.factory);
        SecretsManagerSecretStore secrets = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<SecretInfo> before = await secrets.ListSecretsAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<SecretInfo> after = await secrets.ListSecretsAsync(ct);

        Assert.Equal(before.Select(s => s.Name).Order(), after.Select(s => s.Name).Order());
    }

    /// <summary>The capability the secrets comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task SecretStore_Capability_RoundTrips()
    {
        SecretsManagerSecretStore secrets = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        // SetSecretAsync on a name that does not exist: PutSecretValue answers
        // ResourceNotFoundException and the CreateSecret fallback takes over.
        await secrets.SetSecretAsync(name, "capability round-trip", ct);

        try
        {
            Assert.Contains(name, (await secrets.ListSecretsAsync(ct)).Select(s => s.Name));
            Assert.Equal("capability round-trip", await secrets.GetSecretAsync(name, ct));

            // SetSecretAsync on a name that already exists takes the primary path, PutSecretValue,
            // without ever reaching the CreateSecret fallback.
            await secrets.SetSecretAsync(name, "updated via capability", ct);

            Assert.Equal("updated via capability", await secrets.GetSecretAsync(name, ct));
        }
        finally
        {
            await secrets.DeleteSecretAsync(name, CancellationToken.None);
        }

        Assert.DoesNotContain(name, (await secrets.ListSecretsAsync(ct)).Select(s => s.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render six red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        SecretsManagerDemo demo = new(this.factory);
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
        SecretsManagerDemo demo = new(new SecretsManagerClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
