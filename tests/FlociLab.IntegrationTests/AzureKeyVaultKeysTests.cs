using Azure;
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
/// floci-az's Key Vault router implements <c>/secrets</c> only — see
/// <see cref="AzureKeyVaultSecretsTests"/> for that sample, which authenticates fine and fails for
/// different reasons. Every <c>/keys</c> route here answers a plain 404, never even reaching the
/// response-shape or routing quirks Secrets hits, because the route does not exist at all.
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
    /// A plain 404, not the 501 shape <see cref="ProbeResult.FromException"/> recognises — see
    /// <see cref="CreateKey_Answers_A_Plain_404_Not_The_Storage_Planes_501"/> for the header-level
    /// confirmation that this is genuinely different from an unimplemented-operation response.
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
    /// Every step fails, and there is no cleanup step: <c>CreateKey</c> never returns a key id, so
    /// <c>RunAsync</c>'s <c>finally</c> has nothing to clean up — unlike Key Vault Secrets, where
    /// <c>SetSecret</c> throws only after the secret already exists in the vault.
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
            s => Assert.Equal("Decrypt", s.Title));

        Assert.All(steps, s => Assert.False(s.Succeeded, $"{s.Title} succeeded — floci-az may have shipped Key Vault Keys; update this test and docs/BLAZOR-PLAN.md §14."));
        Assert.Contains("Skipped", steps.Single(s => s.Title == "Encrypt").Error);
        Assert.Contains("Skipped", steps.Single(s => s.Title == "Decrypt").Error);
    }

    /// <summary>
    /// The tripwire for the day floci-az ships <c>/keys</c>: a genuinely unimplemented operation on
    /// this emulator answers 501 with <c>x-ms-error-code: NotImplemented</c> (verified against a
    /// wholly unrouted path); this instead answers a generic router 404, confirming the gap is
    /// "route does not exist" rather than "operation declined".
    /// </summary>
    [Fact]
    public async Task CreateKey_Answers_A_Plain_404_Not_The_Storage_Planes_501()
    {
        KeyClient client = this.factory.Create();

        RequestFailedException ex = await Assert.ThrowsAsync<RequestFailedException>(
            async () => await client.CreateKeyAsync($"flocilab-probe-{Guid.NewGuid():N}", KeyType.Rsa, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(404, ex.Status);
        Assert.Equal("BadRequest", ex.ErrorCode);
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
