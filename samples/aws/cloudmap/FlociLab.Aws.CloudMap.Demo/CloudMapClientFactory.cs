using Amazon.ServiceDiscovery;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.CloudMap;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Cloud Map needs one of its own, turning off the <c>data-</c> host
/// prefix on <c>DiscoverInstances</c>. It is a JSON-RPC service — one <c>POST /</c> with an
/// <c>X-Amz-Target</c> header — so a single base endpoint carries every operation, and nothing
/// else about the SDK usage differs from production.
/// </summary>
public sealed class CloudMapClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonServiceDiscovery Create()
    {
        // Real AWS. The credentials go too — the SDK's own chain (environment, profile, SSO, IMDS)
        // is what a production app uses, and the static "test"/"test" pair would be rejected.
        // Retries come back to the SDK default, because the reason they were off is a
        // lab-ergonomics one that does not apply here.
        if (!endpoints.UseEmulator)
        {
            return new AmazonServiceDiscoveryClient(new AmazonServiceDiscoveryConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonServiceDiscoveryConfig config = new AmazonServiceDiscoveryConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into a minute of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real Cloud Map wants the retries; this is the first of
            // the two emulator-shaped lines in the sample.
            MaxErrorRetry = 0,

            // DiscoverInstances is Cloud Map's data-plane call, and the SDK sends it to
            // data-servicediscovery.<region>.amazonaws.com by prefixing the host with "data-".
            // Prefixing a ServiceURL of 127.0.0.1:4566 yields "data-127.0.0.1", which does not
            // resolve, so the prefix is turned off for the emulator — floci serves both planes on
            // one endpoint. The real-cloud branch above keeps it, as production must.
            DisableHostPrefixInjection = true,
        }.ForFloci(endpoints);

        return new AmazonServiceDiscoveryClient(endpoints.Credentials(), config);
    }
}
