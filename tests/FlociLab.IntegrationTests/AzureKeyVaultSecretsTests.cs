using Azure.Security.KeyVault.Secrets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.KeyVaultSecrets;
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
[Collection(nameof(AzureKeyVaultCollection))]
public sealed class AzureKeyVaultSecretsTests : IAsyncLifetime
{
    private const int FlociAzPort = 4577;

    // A plain ContainerBuilder rather than the FlociBuilder the S3 tests use — see AzureBlobTests
    // for why (Testcontainers.Floci hardcodes port 4566, floci-az listens on 4577).
    private readonly IContainer flociAz = new ContainerBuilder("floci/floci-az:latest")
        .WithPortBinding(FlociAzPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci/health").ForPort(FlociAzPort)))
        .Build();

    private KeyVaultSecretsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociAz.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new KeyVaultSecretsClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociAz.DisposeAsync();

    private string Endpoint => $"http://{this.flociAz.Hostname}:{this.flociAz.GetMappedPublicPort(FlociAzPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new KeyVaultSecretsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new KeyVaultSecretsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListSecrets — before", s.Title),
            s => Assert.Equal("SetSecret", s.Title),
            s => Assert.Equal("GetSecret", s.Title),
            s => Assert.Equal("SetSecret — new version", s.Title),
            s => Assert.Equal("GetSecret — after update", s.Title),
            s => Assert.Equal("DeleteSecret — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Updated from FlociLab.", steps.Single(s => s.Title == "GetSecret — after update").Response);
    }

    /// <summary>
    /// Re-runnable because every run soft-deletes and purges the secret it created, which is what
    /// makes the page safe to hammer during a recording. If this ever fails on the second pass, the
    /// demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Secrets_Behind()
    {
        KeyVaultSecretsDemo demo = new(this.factory);
        KeyVaultSecretStore store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<SecretInfo> before = await store.ListSecretsAsync(ct);
        List<string> deletedBefore = await this.ListDeletedSecretNamesAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<SecretInfo> after = await store.ListSecretsAsync(ct);

        Assert.Equal(before.Select(s => s.Name).Order(), after.Select(s => s.Name).Order());

        // The live list alone cannot see a missing purge — a soft-deleted secret drops out of it
        // either way, and every run's name is unique. The deleted list is where a skipped purge
        // would show up.
        Assert.Equal(deletedBefore.Order(), (await this.ListDeletedSecretNamesAsync(ct)).Order());
    }

    private async Task<List<string>> ListDeletedSecretNamesAsync(CancellationToken ct)
    {
        List<string> names = [];

        await foreach (DeletedSecret secret in this.factory.Create().GetDeletedSecretsAsync(ct))
        {
            names.Add(secret.Name);
        }

        return names;
    }

    /// <summary>The capability the secrets comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task SecretStore_Capability_RoundTrips()
    {
        KeyVaultSecretStore store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        await store.SetSecretAsync(name, "capability round-trip", ct);

        try
        {
            Assert.Contains(name, (await store.ListSecretsAsync(ct)).Select(s => s.Name));
            Assert.Equal("capability round-trip", await store.GetSecretAsync(name, ct));
        }
        finally
        {
            await store.DeleteSecretAsync(name, CancellationToken.None);
        }

        Assert.DoesNotContain(name, (await store.ListSecretsAsync(ct)).Select(s => s.Name));
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
        KeyVaultSecretsDemo demo = new(this.factory);
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
        KeyVaultSecretsDemo demo = new(new KeyVaultSecretsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
