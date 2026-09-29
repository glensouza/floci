using Amazon.AppSync;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.AppSync;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; AppSync needs none of its own.
/// </summary>
public sealed class AppSyncClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// Where a client POSTs GraphQL. AppSync hands this back as <c>Uris["GRAPHQL"]</c> and real
    /// callers use it as given. floci fills the same field in with <c>localhost</c>, which resolves
    /// to <c>::1</c> first and burns the connect timeout on a port Docker only publishes on IPv4
    /// (§14), so the emulator branch builds the identical path from the configured endpoint.
    /// </summary>
    public string GraphqlUrl(string apiId, string? reportedUri)
        => this.UseEmulator || string.IsNullOrEmpty(reportedUri)
            ? $"{this.ServiceUrl}/v1/apis/{apiId}/graphql"
            : reportedUri;

    /// <summary>A fresh client per demo run, so a changed endpoint configuration takes effect.</summary>
    public IAmazonAppSync Create()
    {
        // Real AWS: the SDK's own credential chain, and its default retries.
        if (!endpoints.UseEmulator)
        {
            return new AmazonAppSyncClient(new AmazonAppSyncConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        // Retries off: against a stopped emulator the SDK default turns one refused connection
        // into ~8 s, and the request shown beside each step is meant to be the only one sent.
        AmazonAppSyncConfig config = new AmazonAppSyncConfig { MaxErrorRetry = 0 }.ForFloci(endpoints);

        return new AmazonAppSyncClient(endpoints.Credentials(), config);
    }
}
