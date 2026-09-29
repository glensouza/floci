using Amazon.ElasticLoadBalancingV2;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.ElbV2;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Elastic Load Balancing v2 needs none of its own. It is a Query-protocol
/// service — every call is a form-encoded POST to the base endpoint with an <c>Action</c> field —
/// so nothing else about the SDK usage differs from production.
/// </summary>
public sealed class ElbV2ClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>
    /// The default VPC floci creates in every region. A load balancer cannot be made without
    /// subnets, and listing them would take EC2's SDK package — a second cloud dependency
    /// (docs/BLAZOR-PLAN.md §3, constraint 1) — so the sample names floci's deterministic default
    /// ids instead. Against real AWS these ids do not exist and the first step that needs them
    /// fails with the AWS error, which is the honest outcome: substitute your own VPC and two
    /// subnets in different Availability Zones there.
    /// </summary>
    public string VpcId => $"vpc-default-{endpoints.Region}";

    /// <summary>Two default subnets in different zones — an application load balancer needs at least two.</summary>
    public IReadOnlyList<string> SubnetIds => [$"subnet-default-{endpoints.Region}-a", $"subnet-default-{endpoints.Region}-b"];

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonElasticLoadBalancingV2 Create()
    {
        // Real AWS. The credentials go too — the SDK's own chain (environment, profile, SSO, IMDS)
        // is what a production app uses, and the static "test"/"test" pair would be rejected.
        // Retries come back to the SDK default, because the reason they were off is a
        // lab-ergonomics one that does not apply here.
        if (!endpoints.UseEmulator)
        {
            return new AmazonElasticLoadBalancingV2Client(new AmazonElasticLoadBalancingV2Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonElasticLoadBalancingV2Config config = new AmazonElasticLoadBalancingV2Config
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into a minute of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real ElbV2 wants the retries; this is the second and
            // last emulator-shaped line in the sample.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonElasticLoadBalancingV2Client(endpoints.Credentials(), config);
    }
}
