using Amazon.DynamoDBv2;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.DynamoDb;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; DynamoDB needs none of its own, like SQS — every operation already
/// addresses a single base endpoint and carries its table name in the request body. Nothing else
/// about the SDK usage differs from production.
/// </summary>
public sealed class DynamoDbClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonDynamoDB Create()
    {
        // Real AWS. The credentials go too — the SDK's own chain (environment, profile, SSO, IMDS)
        // is what a production app uses, and the static "test"/"test" pair would be rejected.
        // Retries come back to the SDK default, because the reason they were off is a
        // lab-ergonomics one that does not apply here.
        if (!endpoints.UseEmulator)
        {
            return new AmazonDynamoDBClient(new AmazonDynamoDBConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonDynamoDBConfig config = new AmazonDynamoDBConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into ~49 s of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real DynamoDB wants the retries; this is the second and
            // last emulator-shaped line in the sample.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonDynamoDBClient(endpoints.Credentials(), config);
    }
}
