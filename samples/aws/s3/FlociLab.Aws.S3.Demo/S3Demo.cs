using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FlociLab.Core;

namespace FlociLab.Aws.S3;

/// <summary>
/// Amazon S3 against floci. Ordinary AWSSDK.S3 code — the only emulator-aware line in the sample
/// is in <see cref="S3ClientFactory"/>.
/// </summary>
public sealed class S3Demo(S3ClientFactory factory) : IServiceDemo
{
    private const string ObjectKey = "hello/floci.txt";
    private const string ObjectBody = "Hello from FlociLab.";

    public string Provider => CloudProvider.Aws;

    public string Slug => "s3";

    public string DisplayName => "S3";

    public string Category => "Storage";

    public string Route => "/aws/s3";

    /// <summary>ListBuckets — one request, no state, and it is what the AWS CLI reaches for first.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonS3 client = factory.Create();
            ListBucketsResponse response = await client.ListBucketsAsync(new ListBucketsRequest(), ct).ConfigureAwait(false);
            int count = response.Buckets?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListBuckets returned {count} bucket(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonS3 client = factory.Create();

        // Unique per run, so two runs never collide and a leftover bucket from a crashed run never
        // makes the next one fail. 24 chars, inside S3's 3-63 lowercase-and-hyphens rule.
        string bucket = $"flocilab-s3-{Guid.NewGuid():N}"[..24];
        bool created = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListBuckets — before",
                $"GET {factory.ServiceUrl}/\ns3.ListBucketsAsync(new ListBucketsRequest())",
                async () =>
                {
                    ListBucketsResponse response = await client.ListBucketsAsync(new ListBucketsRequest(), ct).ConfigureAwait(false);
                    IEnumerable<string> names = response.Buckets?.Select(b => $"  {b.BucketName}") ?? [];

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Buckets?.Count ?? 0} bucket(s)\n"
                        + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateBucket",
                $"PUT {factory.ServiceUrl}/{bucket}\ns3.PutBucketAsync(new PutBucketRequest {{ BucketName = \"{bucket}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the PUT lands but the response does not
                    // come back, the bucket exists and cleanup has to know about it. Cleanup
                    // treats an absent bucket as a no-op, so claiming it early is free.
                    created = true;
                    PutBucketResponse response = await client.PutBucketAsync(
                        new PutBucketRequest { BucketName = bucket }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — x-amz-request-id: {response.ResponseMetadata?.RequestId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutObject",
                $"PUT {factory.ServiceUrl}/{bucket}/{ObjectKey}\nContent-Type: text/plain\n\n{ObjectBody}",
                async () =>
                {
                    using MemoryStream body = new(Encoding.UTF8.GetBytes(ObjectBody));
                    PutObjectResponse response = await client.PutObjectAsync(
                        new PutObjectRequest
                        {
                            BucketName = bucket,
                            Key = ObjectKey,
                            InputStream = body,
                            ContentType = "text/plain",
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — ETag: {response.ETag}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListObjectsV2",
                $"GET {factory.ServiceUrl}/{bucket}?list-type=2\ns3.ListObjectsV2Async(...)",
                async () =>
                {
                    ListObjectsV2Response response = await client.ListObjectsV2Async(
                        new ListObjectsV2Request { BucketName = bucket }, ct).ConfigureAwait(false);
                    IEnumerable<string> keys = response.S3Objects?.Select(o => $"  {o.Key} ({o.Size} bytes)") ?? [];

                    return $"HTTP {(int)response.HttpStatusCode} — KeyCount {response.KeyCount}\n"
                        + string.Join('\n', keys);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetObject",
                $"GET {factory.ServiceUrl}/{bucket}/{ObjectKey}\ns3.GetObjectAsync(...)",
                async () =>
                {
                    using GetObjectResponse response = await client.GetObjectAsync(bucket, ObjectKey, ct).ConfigureAwait(false);
                    using StreamReader reader = new(response.ResponseStream);
                    string content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — Content-Type: {response.Headers.ContentType}\n\n{content}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteObject",
                $"DELETE {factory.ServiceUrl}/{bucket}/{ObjectKey}\ns3.DeleteObjectAsync(...)",
                async () =>
                {
                    DeleteObjectResponse response = await client.DeleteObjectAsync(bucket, ObjectKey, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await this.DeleteBucketAsync(client, bucket, ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// The AWS SDK reports both of the interesting failures inside an
    /// <see cref="AmazonServiceException"/>, so <see cref="ProbeResult.FromException"/> — which
    /// inspects only the outermost exception — cannot classify them on its own. A 501 arrives as
    /// a status code on the exception; a refused connection arrives with no status code at all
    /// and a transport exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real S3 would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the bucket.
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Cleanup, which S3 makes a two-step affair: DeleteBucket on a non-empty bucket is a 409, so
    /// every key goes first. The calls use <see cref="CancellationToken.None"/> — a run that was
    /// cancelled still has a bucket to remove.
    /// </summary>
    private async Task<DemoStep> DeleteBucketAsync(IAmazonS3 client, string bucket, CancellationToken ct)
    {
        string request = $"DELETE {factory.ServiceUrl}/{bucket}\ns3.DeleteBucketAsync(\"{bucket}\")";

        return await RunStepAsync("DeleteBucket — cleanup", request, async () =>
        {
            ListObjectsV2Response listed;

            try
            {
                listed = await client.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = bucket }, CancellationToken.None).ConfigureAwait(false);
            }
            // CreateBucket claims the name before it calls, so the bucket may never have been
            // made — that is a clean run to finish, not a cleanup failure worth showing in red.
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
            {
                return "Nothing to remove — the bucket was never created.";
            }

            foreach (S3Object s3Object in listed.S3Objects ?? [])
            {
                await client.DeleteObjectAsync(bucket, s3Object.Key, CancellationToken.None).ConfigureAwait(false);
            }

            DeleteBucketResponse response = await client.DeleteBucketAsync(bucket, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — removed {listed.S3Objects?.Count ?? 0} object(s) and the bucket"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
