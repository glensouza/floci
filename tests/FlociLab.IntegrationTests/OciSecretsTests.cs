using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Oci.Secrets;
using FlociLab.Oci.Vault;
using Microsoft.Extensions.Options;
using Oci.KeymanagementService;
using Oci.KeymanagementService.Models;
using Oci.KeymanagementService.Requests;
using Oci.KeymanagementService.Responses;
using VaultModel = Oci.KeymanagementService.Models.Vault;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-oci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
///
/// <para>
/// The Secrets sample takes its vault and key OCIDs from configuration and carries no
/// <c>OCI.DotNetSDK.Keymanagement</c> to create them, so this fixture provisions both through the
/// Vault sample's own factory first — legitimate here in a way it would not be inside a sample,
/// since the test project already references every RCL and is not the artifact anyone clones.
/// </para>
/// </summary>
public sealed class OciSecretsTests : IAsyncLifetime
{
    private const int FlociOciPort = 4599;

    // A plain ContainerBuilder, not FlociBuilder — see OciObjectStorageTests for why: that type
    // hardcodes port 4566, and floci-oci listens on 4599 with a namespaced health path.
    private readonly IContainer flociOci = new ContainerBuilder("floci/floci-oci:latest")
        .WithPortBinding(FlociOciPort, assignRandomHostPort: true)
        .WithEnvironment("FLOCI_OCI_DEFAULT_TENANCY_ID", OciEmulatorOptions.DefaultTenancyId)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-oci/health").ForPort(FlociOciPort)))
        .Build();

    private SecretsClientFactory factory = null!;
    private string vaultId = null!;
    private string keyId = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociOci.StartAsync(TestContext.Current.CancellationToken);

        (this.vaultId, this.keyId) = await this.ProvisionVaultAndKeyAsync(TestContext.Current.CancellationToken);
        this.factory = new SecretsClientFactory(EndpointsFor(this.Endpoint, this.vaultId, this.keyId));
    }

    public async ValueTask DisposeAsync() => await this.flociOci.DisposeAsync();

    // Hostname rather than "localhost" deliberately — see OciVaultTests for why.
    private string Endpoint => $"http://{this.flociOci.Hostname}:{this.flociOci.GetMappedPublicPort(FlociOciPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new SecretsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SecretsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListSecrets — before", s.Title),
            s => Assert.Equal("CreateSecret", s.Title),
            s => Assert.Equal("GetSecretBundle", s.Title),
            s => Assert.Equal("UpdateSecret", s.Title),
            s => Assert.Equal("GetSecretBundle — after update", s.Title),
            s => Assert.Equal("ScheduleSecretDeletion — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Updated from FlociLab.", steps.Single(s => s.Title == "GetSecretBundle — after update").Response);
    }

    /// <summary>
    /// Re-runnable because every run creates its own uniquely-named secret. Real OCI Vault never
    /// deletes anything on request, only schedules it days out, so this does not assert "leaves
    /// nothing behind" — the tripwire is that both runs complete the same way.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice()
    {
        SecretsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        for (int run = 0; run < 2; run++)
        {
            List<DemoStep> steps = [];

            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                steps.Add(step);
            }

            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {run}, {s.Title}: {s.Error}"));
        }
    }

    /// <summary>
    /// The vault and key are configuration, so an unset one is a setup gap rather than anything the
    /// emulator did. It has to reach the page as a named, actionable failed step — a bare
    /// MissingParameter 400 from floci-oci reads as a broken sample. This also pins that the run
    /// still reaches its cleanup step rather than dropping out of the iterator.
    /// </summary>
    [Fact]
    public async Task Unconfigured_Vault_Fails_The_CreateSecret_Step_By_Name()
    {
        SecretsClientFactory unconfigured = new(EndpointsFor(this.Endpoint, vaultId: null, keyId: null));
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SecretsDemo(unconfigured).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.True(s.Succeeded, s.Error),
            s => Assert.False(s.Succeeded, "CreateSecret should fail when no vault is configured."));

        DemoStep createSecret = steps[1];

        Assert.Equal("CreateSecret", createSecret.Title);
        Assert.Contains("Floci:Oci:VaultId", createSecret.Error);
        Assert.Contains("Floci:Oci:KeyId", createSecret.Error);
    }

    /// <summary>The capability the secrets comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task SecretStore_Capability_RoundTrips()
    {
        OciSecretsStore secretStore = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        await secretStore.SetSecretAsync(name, "capability round-trip", ct);

        try
        {
            Assert.Contains(name, (await secretStore.ListSecretsAsync(ct)).Select(s => s.Name));
            Assert.Equal("capability round-trip", await secretStore.GetSecretAsync(name, ct));

            await secretStore.SetSecretAsync(name, "capability round-trip, updated", ct);

            Assert.Equal("capability round-trip, updated", await secretStore.GetSecretAsync(name, ct));
        }
        finally
        {
            await secretStore.DeleteSecretAsync(name, CancellationToken.None);
        }
    }

    /// <summary>
    /// The capability creates no vault of its own — it writes into the configured one. This pins
    /// that: a write through the capability leaves the emulator with exactly the one vault the
    /// fixture provisioned, which is what keeps OCI.DotNetSDK.Keymanagement out of the sample.
    /// </summary>
    [Fact]
    public async Task SecretStore_Capability_Creates_No_Vault_Of_Its_Own()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-novault-{Guid.NewGuid():N}";

        await new OciSecretsStore(this.factory).SetSecretAsync(name, "a", ct);

        try
        {
            using KmsVaultClient vaultClient = this.VaultFactory().CreateVault();
            ListVaultsResponse vaults = await vaultClient.ListVaults(
                new ListVaultsRequest { CompartmentId = this.factory.CompartmentId }, cancellationToken: ct);

            Assert.Single(vaults.Items, v => v.LifecycleState == VaultSummary.LifecycleStateEnum.Active);
        }
        finally
        {
            await new OciSecretsStore(this.factory).DeleteSecretAsync(name, CancellationToken.None);
        }
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        SecretsDemo demo = new(this.factory);
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
    /// A run that cannot even build its client has to render the reason rather than take the page
    /// down with it — the same guard <c>VaultDemo</c> needs, for the same reason.
    /// </summary>
    [Fact]
    public async Task Client_Construction_Failure_Becomes_A_Failed_Step()
    {
        SecretsClientFactory refusing = new(new OciEndpoints(
            Options.Create(new FlociOptions { Oci = new OciEmulatorOptions { UseEmulator = false } })));

        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SecretsDemo(refusing).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        DemoStep only = Assert.Single(steps);

        Assert.False(only.Succeeded);
        Assert.Contains("TenancyId", only.Error);
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is reserved
    /// and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        SecretsDemo demo = new(new SecretsClientFactory(EndpointsFor("http://127.0.0.1:1", null, null)));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// Creates the vault and key the Secrets sample expects to be handed. Uses the Vault sample's
    /// factory rather than a hand-rolled client so the two samples agree about how floci-oci's
    /// per-vault management endpoint is reached (plan §14).
    /// </summary>
    private async Task<(string VaultId, string KeyId)> ProvisionVaultAndKeyAsync(CancellationToken ct)
    {
        VaultClientFactory vaultFactory = this.VaultFactory();
        using KmsVaultClient vaultClient = vaultFactory.CreateVault();

        CreateVaultResponse created = await vaultClient.CreateVault(
            new CreateVaultRequest
            {
                CreateVaultDetails = new CreateVaultDetails
                {
                    CompartmentId = OciEmulatorOptions.DefaultTenancyId,
                    DisplayName = $"flocilab-secrets-fixture-{Guid.NewGuid():N}",
                    VaultType = CreateVaultDetails.VaultTypeEnum.Default,
                },
            },
            cancellationToken: ct);

        // CreateVault answers before the vault is ACTIVE — see the OCI Vault sample.
        GetVaultResponse active = await vaultClient.Waiters
            .ForVault(new GetVaultRequest { VaultId = created.Vault.Id }, VaultModel.LifecycleStateEnum.Active)
            .ExecuteAsync();

        using KmsManagementClient management = vaultFactory.CreateManagement(active.Vault.ManagementEndpoint);
        CreateKeyResponse key = await management.CreateKey(
            new CreateKeyRequest
            {
                CreateKeyDetails = new CreateKeyDetails
                {
                    CompartmentId = OciEmulatorOptions.DefaultTenancyId,
                    DisplayName = $"flocilab-secrets-fixture-key-{Guid.NewGuid():N}",
                    KeyShape = new KeyShape { Algorithm = KeyShape.AlgorithmEnum.Aes, Length = 32 },
                    ProtectionMode = CreateKeyDetails.ProtectionModeEnum.Software,
                },
            },
            cancellationToken: ct);

        return (active.Vault.Id, key.Key.Id);
    }

    private VaultClientFactory VaultFactory() => new(EndpointsFor(this.Endpoint, null, null));

    private static OciEndpoints EndpointsFor(string endpoint, string? vaultId, string? keyId)
        => new(Options.Create(new FlociOptions
        {
            Oci = new OciEmulatorOptions { Endpoint = endpoint, VaultId = vaultId, KeyId = keyId },
        }));
}
