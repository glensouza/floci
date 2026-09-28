using Azure.Security.KeyVault.Keys;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.KeyVaultKeys;
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
/// Through floci-az 0.12.0, the Key Vault router implemented <c>/secrets</c> only and every
/// <c>/keys</c> route answered a plain 404 — see <see cref="AzureKeyVaultSecretsTests"/> for the
/// sample that authenticates fine and fails for different reasons. 0.13.0 added real
/// <c>/keys</c> routing, confirmed 2026-09-28, but <c>CreateKey</c> now hits the very
/// response-shape bug Secrets used to have before 0.13.0 also fixed it there: floci-az still sends
/// <c>attributes.nbf</c>/<c>attributes.exp</c> as JSON <c>null</c> on the Keys plane, so the SDK's
/// model throws parsing it (§14). Key Vault Keys remains unusable, just for a different reason than
/// before.
/// </summary>
[Collection(nameof(AzureKeyVaultCollection))]
public sealed class AzureKeyVaultKeysTests : IAsyncLifetime
{
    private const int FlociAzPort = 4577;

    private readonly IContainer flociAz = new ContainerBuilder("floci/floci-az:latest")
        .WithPortBinding(FlociAzPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci/health").ForPort(FlociAzPort)))
        .Build();

    private KeyVaultKeysClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociAz.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new KeyVaultKeysClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociAz.DisposeAsync();

    private string Endpoint => $"http://{this.flociAz.Hostname}:{this.flociAz.GetMappedPublicPort(FlociAzPort)}";

    /// <summary>
    /// Not the 501 shape <see cref="ProbeResult.FromException"/> recognises as NotImplemented — the
    /// <c>/keys</c> route exists since 0.13.0 and answers, but badly (see
    /// <see cref="CreateKey_Throws_Because_Nbf_And_Exp_Are_Null_Not_Omitted"/>).
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Error()
    {
        ProbeResult result = await new KeyVaultKeysDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

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
        KeyVaultKeysDemo demo = new(new KeyVaultKeysClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// Every step fails, cleanup included. <c>CreateKey</c> creates the key server-side and then
    /// throws parsing the response, so no key id ever comes back — which is why <c>RunAsync</c>
    /// claims the key for cleanup before the call rather than on the id. Without that, every run
    /// would leave a key behind in the lab's persistent volume with no red step to say so.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Documents_Keys_Are_Not_Implemented()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new KeyVaultKeysDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListKeys — before", s.Title),
            s => Assert.Equal("CreateKey", s.Title),
            s => Assert.Equal("Encrypt", s.Title),
            s => Assert.Equal("Decrypt", s.Title),
            s => Assert.Equal("DeleteKey — cleanup", s.Title));

        Assert.All(steps, s => Assert.False(s.Succeeded, $"{s.Title} succeeded — floci-az may have shipped Key Vault Keys; update this test and docs/BLAZOR-PLAN.md §14."));
        Assert.Contains("Skipped", steps.Single(s => s.Title == "Encrypt").Error);
        Assert.Contains("Skipped", steps.Single(s => s.Title == "Decrypt").Error);

        // Cleanup is attempted, and fails on the same null-timestamp parse as CreateKey, because
        // the delete response carries the same attributes block.
        Assert.Contains("'Null'", steps[^1].Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// floci-az 0.13.0 added real <c>/keys</c> routing (confirmed 2026-09-28) — this used to assert
    /// the plain 404 that meant the route did not exist at all. Now the request reaches floci-az's
    /// handler and is accepted server-side, but the response carries the same unset-timestamp bug
    /// Key Vault Secrets had before 0.13.0 fixed it there: <c>attributes.nbf</c>/<c>attributes.exp</c>
    /// come back as JSON <c>null</c> rather than omitted, and the SDK's model requires a number. This
    /// is now the tripwire for the day floci-az fixes the Keys plane the same way it already fixed
    /// Secrets.
    /// </summary>
    [Fact]
    public async Task CreateKey_Throws_Because_Nbf_And_Exp_Are_Null_Not_Omitted()
    {
        KeyClient client = this.factory.Create();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.CreateKeyAsync($"flocilab-probe-{Guid.NewGuid():N}", KeyType.Rsa, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("'Number'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Null'", ex.Message, StringComparison.Ordinal);
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
