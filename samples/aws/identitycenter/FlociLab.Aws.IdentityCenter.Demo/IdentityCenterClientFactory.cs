using Amazon;
using Amazon.SSOAdmin;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.IdentityCenter;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; IAM Identity Center needs none of its own. The SDK's service name is
/// SSO Admin, a JSON 1.1 service whose <c>X-Amz-Target</c> prefix is <c>SWBExternalService</c>.
/// </summary>
public sealed class IdentityCenterClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonSSOAdmin Create()
    {
        // Real AWS. An Identity Center instance lives in exactly one region, the one it was enabled
        // in, so the configured region is used. The credentials go: the SDK's own chain is what a
        // production app uses, and the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonSSOAdminClient(new AmazonSSOAdminConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonSSOAdminConfig config = new AmazonSSOAdminConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s. A page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request. A production app against real Identity Center wants the retries.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonSSOAdminClient(endpoints.Credentials(), config);
    }
}
