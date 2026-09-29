using Amazon.ElasticLoadBalancing;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.ElbClassic;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Classic Elastic Load Balancing needs none of its own. It is a Query-protocol
/// service — every call is a form-encoded POST to the base endpoint with an <c>Action</c> field —
/// so nothing else about the SDK usage differs from production.
/// </summary>
public sealed class ElbClassicClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>
    /// A classic load balancer names Availability Zones rather than subnets, which is what lets this
    /// sample stay clear of EC2's SDK package (docs/BLAZOR-PLAN.md §3, constraint 1) — the zone name
    /// is derivable from the region. Against real AWS the region needs a default VPC with an "a"
    /// zone; substitute a zone your account can use if it has none.
    /// </summary>
    public string AvailabilityZone => $"{endpoints.Region}a";

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonElasticLoadBalancing Create()
    {
        // Real AWS. The credentials go too — the SDK's own chain (environment, profile, SSO, IMDS)
        // is what a production app uses, and the static "test"/"test" pair would be rejected.
        // Retries come back to the SDK default, because the reason they were off is a
        // lab-ergonomics one that does not apply here.
        if (!endpoints.UseEmulator)
        {
            return new AmazonElasticLoadBalancingClient(new AmazonElasticLoadBalancingConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonElasticLoadBalancingConfig config = new AmazonElasticLoadBalancingConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into a minute of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real ElbClassic wants the retries; this is the second and
            // last emulator-shaped line in the sample.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonElasticLoadBalancingClient(endpoints.Credentials(), config);
    }
}
