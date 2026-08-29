using Amazon.S3;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.S3;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; <c>ForcePathStyle</c> is the one S3 adds; <c>MaxErrorRetry</c> is
/// a lab-ergonomics choice rather than a compatibility one, explained at its assignment. Nothing
/// else about the SDK usage differs from production.
/// </summary>
public sealed class S3ClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonS3 Create()
    {
        AmazonS3Config config = new AmazonS3Config
        {
            // Without this the SDK addresses buckets as https://my-bucket.localhost:4566/, which
            // resolves nowhere. Path style keeps them at http://localhost:4566/my-bucket/. Real
            // S3 deprecated path style for new buckets; every S3-compatible emulator needs it.
            ForcePathStyle = true,

            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into ~49 s of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real S3 wants the retries; this is the second and last
            // emulator-shaped line in the sample.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonS3Client(endpoints.Credentials(), config);
    }
}
