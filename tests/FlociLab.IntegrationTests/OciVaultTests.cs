using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Oci.Vault;
using Microsoft.Extensions.Options;
using Oci.KeymanagementService;
using Oci.KeymanagementService.Models;
using Oci.KeymanagementService.Requests;
using Oci.KeymanagementService.Responses;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-oci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class OciVaultTests : IAsyncLifetime
{
    private const int FlociOciPort = 4599;

    // A plain ContainerBuilder, not FlociBuilder — see OciObjectStorageTests for why: that type
    // hardcodes port 4566, and floci-oci listens on 4599 with a namespaced health path.
    private readonly IContainer flociOci = new ContainerBuilder("floci/floci-oci:latest")
        .WithPortBinding(FlociOciPort, assignRandomHostPort: true)
        // The tenancy OCID the lab uses everywhere. The image issues none of its own and verifies
        // nothing, but passing it keeps the container's idea of the tenancy and the sample's
        // compartment OCID the same value, which is what the AppHost does too.
        .WithEnvironment("FLOCI_OCI_DEFAULT_TENANCY_ID", OciEmulatorOptions.DefaultTenancyId)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-oci/health").ForPort(FlociOciPort)))
        .Build();

    private VaultClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociOci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new VaultClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociOci.DisposeAsync();

    // Hostname rather than "localhost" deliberately. Testcontainers hands back an address, and on
    // a Windows host "localhost" resolves to ::1 first while the published port is IPv4-only —
    // every first connection then eats a ~2 s dead IPv6 attempt before falling back.
    private string Endpoint => $"http://{this.flociOci.Hostname}:{this.flociOci.GetMappedPublicPort(FlociOciPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new VaultDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new VaultDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListVaults — before", s.Title),
            s => Assert.Equal("CreateVault", s.Title),
            s => Assert.Equal("CreateKey", s.Title),
            s => Assert.Equal("Encrypt", s.Title),
            s => Assert.Equal("Decrypt", s.Title),
            s => Assert.Equal("ScheduleKeyDeletion — cleanup", s.Title),
            s => Assert.Equal("ScheduleVaultDeletion — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "Decrypt").Response);
    }

    /// <summary>
    /// Re-runnable because every run creates its own uniquely-named vault, which is what makes the
    /// page safe to hammer during a recording. Unlike Queue's round trip, this does not assert
    /// "leaves nothing behind" — real OCI Vault never deletes anything on request, only schedules
    /// it days out (see <c>VaultDemo</c>'s remarks), so both runs leave their vault listed in
    /// <c>PENDING_DELETION</c>. The tripwire here is that both runs complete the same way.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice()
    {
        VaultDemo demo = new(this.factory);
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

    /// <summary>The capability the key-management comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task KeyManagement_Capability_RoundTrips()
    {
        OciVault keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        string keyId = await keyManagement.CreateKeyAsync(name, ct);

        try
        {
            Assert.Contains(keyId, (await keyManagement.ListKeysAsync(ct)).Select(k => k.Id));

            byte[] plaintext = "capability round-trip"u8.ToArray();
            byte[] ciphertext = await keyManagement.EncryptAsync(keyId, plaintext, ct);

            Assert.NotEqual(plaintext, ciphertext);
            Assert.Equal(plaintext, await keyManagement.DecryptAsync(keyId, ciphertext, ct));
        }
        finally
        {
            await keyManagement.DeleteKeyAsync(keyId, CancellationToken.None);
        }
    }

    /// <summary>
    /// The capability after a demo run — the ordinary sequence the app produces, and the one that
    /// floci-oci's missing host routing (§14) could plausibly break. <c>OciVault</c> reuses its
    /// fixed <c>flocilab</c> vault rather than creating one, so if a <c>VaultDemo</c> run left a
    /// newer <c>ACTIVE</c> vault behind, the emulator would attribute the capability's next
    /// <c>CreateKey</c> to that vault instead. It does not, and this pins why: the demo schedules
    /// its own vault for deletion in its <c>finally</c>, so no newer <c>ACTIVE</c> vault survives
    /// the run. The warm-up call makes the test meaningful rather than accidental — it creates
    /// <c>flocilab</c> <em>before</em> the demo run, so the second <c>CreateKeyAsync</c> genuinely
    /// takes the reuse path instead of creating the most-recent vault itself.
    ///
    /// <para>
    /// So this is the tripwire for the demo's vault cleanup: delete that cleanup and the capability
    /// silently starts writing keys into the demo's vault. Concurrency is a separate matter —
    /// <see cref="CreateKey_Routes_To_The_Most_Recently_Created_Vault_When_Two_Are_Alive"/> pins the
    /// routing itself, and a capability call genuinely overlapping a demo run remains the one shape
    /// §14 records as unfixable from this side.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Capability_Lists_Its_Own_Key_After_A_Demo_Run()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OciVault keyManagement = new(this.factory);

        string warmUpKeyId = await keyManagement.CreateKeyAsync($"flocilab-warmup-{Guid.NewGuid():N}", ct);

        await foreach (DemoStep _ in new VaultDemo(this.factory).RunAsync(ct))
        {
            // The steps themselves are asserted by RoundTrip_Every_Step_Succeeds; this run is here
            // only for the newer vault it leaves behind.
        }

        string keyId = await keyManagement.CreateKeyAsync($"flocilab-after-demo-{Guid.NewGuid():N}", ct);

        try
        {
            IReadOnlyList<KeyInfo> keys = await keyManagement.ListKeysAsync(ct);

            Assert.Contains(keyId, keys.Select(k => k.Id));
        }
        finally
        {
            await keyManagement.DeleteKeyAsync(keyId, CancellationToken.None);
            await keyManagement.DeleteKeyAsync(warmUpKeyId, CancellationToken.None);
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
        VaultDemo demo = new(this.factory);
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
    /// A run that cannot even build its vault client has to render the reason rather than take the
    /// page down with it. <c>VaultClientFactory.CreateVault()</c> refuses real-cloud mode with the
    /// lab's synthetic tenancy, and that refusal happens before the first request — so if the
    /// construction ever moves back outside <c>RunAsync</c>'s try, the iterator throws on the first
    /// <c>MoveNextAsync</c>, escapes the page's <c>OperationCanceledException</c>-only catch, and
    /// kills the Blazor circuit instead of showing a failed step.
    /// </summary>
    [Fact]
    public async Task Client_Construction_Failure_Becomes_A_Failed_Step()
    {
        VaultClientFactory refusing = new(new OciEndpoints(
            Options.Create(new FlociOptions { Oci = new OciEmulatorOptions { UseEmulator = false } })));

        List<DemoStep> steps = [];

        await foreach (DemoStep step in new VaultDemo(refusing).RunAsync(TestContext.Current.CancellationToken))
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
        VaultDemo demo = new(new VaultClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// Real OCI's <c>CreateKeyDetails</c> carries no <c>VaultId</c> field at all — a key belongs to
    /// whichever vault's management endpoint receives the request, the same host-routed shape OCI
    /// Queue uses for its <c>messagesEndpoint</c>. floci-oci does not actually host-route per vault
    /// (<c>VaultClientFactory.CreateManagement</c>'s remarks — every plane lands on the same
    /// emulator address), so it falls back to associating a new key with whichever vault was
    /// created most recently. Verified by curl against floci-oci 0.3.0, 2026-09-02. Safe for this
    /// sample's own sequential create-vault-then-create-key flow; pinned here so a capability call
    /// racing a demo run — two vaults alive at once — is a known risk rather than a surprise
    /// (docs/BLAZOR-PLAN.md §14).
    /// </summary>
    [Fact]
    public async Task CreateKey_Routes_To_The_Most_Recently_Created_Vault_When_Two_Are_Alive()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        KmsVaultClient vault = this.factory.CreateVault();

        CreateVaultResponse first = await CreateActiveVaultAsync(vault, "flocilab-route-a", ct);
        CreateVaultResponse second = await CreateActiveVaultAsync(vault, "flocilab-route-b", ct);

        using KmsManagementClient management = this.factory.CreateManagement(second.Vault.ManagementEndpoint);
        CreateKeyResponse key = await management.CreateKey(
            new CreateKeyRequest
            {
                CreateKeyDetails = new CreateKeyDetails
                {
                    CompartmentId = this.factory.CompartmentId,
                    DisplayName = $"flocilab-route-key-{Guid.NewGuid():N}",
                    KeyShape = new KeyShape { Algorithm = KeyShape.AlgorithmEnum.Aes, Length = 32 },
                    ProtectionMode = CreateKeyDetails.ProtectionModeEnum.Software,
                },
            },
            cancellationToken: ct);

        Assert.Equal(second.Vault.Id, key.Key.VaultId);
        Assert.NotEqual(first.Vault.Id, key.Key.VaultId);
    }

    private static async Task<CreateVaultResponse> CreateActiveVaultAsync(KmsVaultClient vault, string name, CancellationToken ct)
    {
        CreateVaultResponse created = await vault.CreateVault(
            new CreateVaultRequest
            {
                CreateVaultDetails = new CreateVaultDetails
                {
                    CompartmentId = OciEmulatorOptions.DefaultTenancyId,
                    DisplayName = name,
                    VaultType = CreateVaultDetails.VaultTypeEnum.Default,
                },
            },
            cancellationToken: ct);

        await vault.Waiters.ForVault(new GetVaultRequest { VaultId = created.Vault.Id }, Vault.LifecycleStateEnum.Active).ExecuteAsync();

        return created;
    }

    private static OciEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Oci = new OciEmulatorOptions { Endpoint = endpoint } }));
}
