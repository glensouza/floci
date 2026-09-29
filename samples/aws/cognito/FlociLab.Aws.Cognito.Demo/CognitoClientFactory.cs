using Amazon;
using Amazon.CognitoIdentityProvider;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.Cognito;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Cognito user pools need none of their own, because every operation
/// addresses a single base endpoint. Nothing else about the SDK usage differs from production.
/// </summary>
public sealed class CognitoClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run, signed with the lab's own credentials (or the SDK's chain
    /// against real AWS). Production would hold one for the process lifetime; a page that can be
    /// re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonCognitoIdentityProvider Create()
    {
        // Real AWS. The credentials go too — the SDK's own chain (environment, profile, SSO, IMDS)
        // is what a production app uses, and the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonCognitoIdentityProviderClient(new AmazonCognitoIdentityProviderConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region) });
        }

        return new AmazonCognitoIdentityProviderClient(endpoints.Credentials(), this.EmulatorConfig());
    }

    private AmazonCognitoIdentityProviderConfig EmulatorConfig()
        => new AmazonCognitoIdentityProviderConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into ~a minute of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real Cognito wants the retries; this is the second and last
            // emulator-shaped line in the sample.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);
}
