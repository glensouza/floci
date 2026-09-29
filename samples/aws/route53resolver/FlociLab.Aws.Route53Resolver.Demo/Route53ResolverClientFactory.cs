using Amazon.Route53Resolver;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.Route53Resolver;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Route 53 Resolver needs none of its own. Its JSON-protocol calls (<c>X-Amz-Target: Route53Resolver.*</c>)
/// all go to one base endpoint, so nothing else about the SDK usage differs from production.
/// </summary>
public sealed class Route53ResolverClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonRoute53Resolver Create()
    {
        // Real AWS. The credentials go too — the SDK's own chain (environment, profile, SSO, IMDS)
        // is what a production app uses, and the static "test"/"test" pair would be rejected.
        // Retries come back to the SDK default, because the reason they were off is a
        // lab-ergonomics one that does not apply here.
        if (!endpoints.UseEmulator)
        {
            return new AmazonRoute53ResolverClient(new AmazonRoute53ResolverConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonRoute53ResolverConfig config = new AmazonRoute53ResolverConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into a minute of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real Route 53 Resolver wants the retries; this is the second and
            // last emulator-shaped line in the sample.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonRoute53ResolverClient(endpoints.Credentials(), config);
    }
}
