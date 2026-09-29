using Amazon;
using Amazon.GlobalAccelerator;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.GlobalAccelerator;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Global Accelerator needs none of its own. It is a JSON 1.1 service —
/// every call is a POST to the base endpoint with an <c>X-Amz-Target</c> header — so nothing else
/// about the SDK usage differs from production.
/// </summary>
public sealed class GlobalAcceleratorClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonGlobalAccelerator Create()
    {
        // Real AWS. Global Accelerator's control plane exists in us-west-2 and nowhere else, so the
        // configured region is deliberately not used here; where the endpoints live is the endpoint
        // group's own region, which the demo sets. The credentials go too: the SDK's own chain is what a production app uses, and the
        // static "test"/"test" pair would be rejected. Retries come back to the SDK default.
        if (!endpoints.UseEmulator)
        {
            return new AmazonGlobalAcceleratorClient(new AmazonGlobalAcceleratorConfig
            {
                RegionEndpoint = RegionEndpoint.USWest2,
            });
        }

        AmazonGlobalAcceleratorConfig config = new AmazonGlobalAcceleratorConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into a minute of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real Global Accelerator wants the retries; this is the
            // second and last emulator-shaped line in the sample.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonGlobalAcceleratorClient(endpoints.Credentials(), config);
    }
}
