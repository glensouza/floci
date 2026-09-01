using Azure;
using Azure.Security.KeyVault.Secrets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.KeyVaultSecrets;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-az per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
///
/// The client authenticates fine against floci-az's real IMDS token endpoint (§14 covers the
/// TLS-check and challenge-resource-verification workarounds this needed), but every operation
/// still fails for two unrelated, floci-az-side reasons pinned below.
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

    /// <summary>
    /// The classification the coverage matrix shows today: a clean 404, not the 501 shape
    /// <see cref="ProbeResult.FromException"/> would recognise, because floci-az's router
    /// misinterprets the SDK's trailing-slash list request as "get a secret named the empty
    /// string" — see <see cref="ListSecrets_Is_Misrouted_As_GetSecret_With_An_Empty_Name"/>.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Error()
    {
        ProbeResult result = await new KeyVaultSecretsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Error, result.Status);
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

    /// <summary>
    /// Every step fails today, for the two reasons documented on <see cref="KeyVaultSecretsDemo"/>
    /// and pinned individually below — the cleanup step included, which is why there are six.
    ///
    /// <para>
    /// Cleanup still runs (and still fails) because <c>SetSecret</c> throws on the response it gets
    /// back rather than never sending the request: the PUT lands, so the secret genuinely exists in
    /// the vault, and <c>RunAsync</c> claims it for cleanup before the call — the same "created
    /// appears before cleanup claims it" ordering Queue Storage uses. The secret is therefore left
    /// behind in the vault, which costs nothing against a throwaway container and is the honest
    /// outcome to pin.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RoundTrip_Documents_Key_Vault_Secrets_Is_Not_Yet_Usable()
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

        Assert.All(steps, s => Assert.False(s.Succeeded, $"{s.Title} succeeded — floci-az may have fixed Key Vault Secrets; update this test and docs/BLAZOR-PLAN.md §14."));
    }

    /// <summary>
    /// The list-specific half of the gap. Confirmed by curling <c>GET /secrets</c> (no trailing
    /// slash) directly, which answers <c>{"value":[],"nextLink":null}</c> — floci-az can list
    /// secrets, it just does not recognise the shape the real SDK actually sends for it.
    /// </summary>
    [Fact]
    public async Task ListSecrets_Is_Misrouted_As_GetSecret_With_An_Empty_Name()
    {
        SecretClient client = this.factory.Create();

        RequestFailedException ex = await Assert.ThrowsAsync<RequestFailedException>(async () =>
        {
            await foreach (SecretProperties _ in client.GetPropertiesOfSecretsAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
            {
            }
        });

        Assert.Equal(404, ex.Status);
        Assert.Equal("SecretNotFound", ex.ErrorCode);
    }

    /// <summary>
    /// The response-shape half of the gap: floci-az sends <c>attributes.nbf</c>/<c>exp</c> as JSON
    /// <c>null</c> for a secret with no explicit expiry, and the SDK's model requires a number
    /// there. This is the tripwire for the day floci-az starts omitting the fields instead.
    /// </summary>
    [Fact]
    public async Task SetSecret_Throws_Because_Nbf_And_Exp_Are_Null_Not_Omitted()
    {
        SecretClient client = this.factory.Create();
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.SetSecretAsync(name, "capability round-trip", TestContext.Current.CancellationToken));

        Assert.Contains("'Number'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Null'", ex.Message, StringComparison.Ordinal);
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
