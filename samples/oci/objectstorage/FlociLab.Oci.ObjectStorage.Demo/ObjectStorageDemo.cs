using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using FlociLab.Core;
using Oci.Common.Model;
using Oci.ObjectstorageService;
using Oci.ObjectstorageService.Models;
using Oci.ObjectstorageService.Requests;
using Oci.ObjectstorageService.Responses;

namespace FlociLab.Oci.ObjectStorage;

/// <summary>
/// OCI Object Storage against floci-oci. Ordinary OCI.DotNetSDK.Objectstorage code — the only
/// emulator-aware lines in the sample are in <see cref="ObjectStorageClientFactory"/>.
///
/// <para>
/// Two things here are unlike the S3, Blob and GCS samples. First, nothing can be addressed until
/// you know the tenancy's Object Storage <em>namespace</em>, which is a value you look up rather
/// than configure — so the run starts with a <c>GetNamespace</c> step that has no analog in the
/// other three. Second, buckets live in a compartment rather than in an account, so create and
/// list both carry a compartment OCID.
/// </para>
///
/// <para>
/// A naming note that reads as a mistake and is not: the SDK's operations are
/// <c>client.GetNamespace(...)</c>, not <c>GetNamespaceAsync(...)</c>, and they return
/// <c>Task&lt;T&gt;</c> anyway. Awaiting a method with no Async suffix is how this SDK is used.
/// </para>
/// </summary>
public sealed class ObjectStorageDemo(ObjectStorageClientFactory factory) : IServiceDemo
{
    private const string ObjectName = "hello/floci.txt";
    private const string ObjectBody = "Hello from FlociLab.";
    private const string ObjectContentType = "text/plain";

    public string Provider => CloudProvider.Oci;

    public string Slug => "objectstorage";

    public string DisplayName => "Object Storage";

    public string Category => "Storage";

    public string Route => "/oci/objectstorage";

    /// <summary>
    /// GetNamespace — the cheapest call the service answers, and the one every OCI program makes
    /// first anyway. It needs no compartment and no bucket, so a failure is unambiguously about
    /// the service rather than about what the lab asked for.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using ObjectStorageClient client = factory.Create();
            GetNamespaceResponse response = await client.GetNamespace(new GetNamespaceRequest(), cancellationToken: ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetNamespace returned \"{response.Value}\".");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Building the client can itself fail: the real-cloud branch of the factory refuses a run
        // that would create buckets in the lab's synthetic compartment. That has to become a
        // failed step like any other — an iterator that throws on the first MoveNextAsync takes
        // down the circuit instead of rendering the reason. Caught here and yielded below,
        // because C# forbids a yield inside a try that has a catch.
        ObjectStorageClient? constructed = null;
        Exception? clientFailure = null;

        try
        {
            constructed = factory.Create();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clientFailure = ex;
        }

        if (constructed is null)
        {
            yield return DemoStep.Failed(
                "ObjectStorageClient",
                clientFailure!,
                "new ObjectStorageClient(endpoints.AuthenticationProvider())");

            yield break;
        }

        using ObjectStorageClient client = constructed;

        // Asked of the client rather than taken from the factory, so the request lines below say
        // where the bytes actually went. In emulator mode ForFloci has set both the endpoint and
        // the realm template to the emulator, so this is the emulator; in real-cloud mode the
        // sample sets no endpoint at all and this is whatever the SDK resolved from the region.
        // Reading factory.Endpoint instead would print http://localhost:4599 above a run that
        // reached Oracle — the exact confusion the three ledes on this page are about.
        string origin = client.GetEndpoint().ToString().TrimEnd('/');

        // Unique per run, so two runs never collide and a leftover bucket from a crashed run never
        // makes the next one fail.
        string bucket = $"flocilab-oci-{Guid.NewGuid():N}"[..24];
        string? space = null;
        bool created = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "GetNamespace",
                $"GET {origin}/n/\nobjectStorage.GetNamespace(new GetNamespaceRequest())",
                async () =>
                {
                    GetNamespaceResponse response = await client.GetNamespace(new GetNamespaceRequest(), cancellationToken: ct).ConfigureAwait(false);
                    space = response.Value;

                    // The response body really is a bare JSON string. Every path below is built
                    // from it, which is why this step comes first and the other three samples
                    // have nothing like it.
                    return $"\"{response.Value}\"\n\nEvery path below is /n/{response.Value}/...";
                }).ConfigureAwait(false);

            // The namespace lookup failed, so there is nothing to address. Stop rather than
            // emitting five more steps whose only news is a null in the URL.
            if (space is null)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "ListBuckets — before",
                $"GET {origin}/n/{space}/b/?compartmentId={factory.CompartmentId}\nobjectStorage.ListBuckets(new ListBucketsRequest {{ NamespaceName, CompartmentId }})",
                async () =>
                {
                    ListBucketsResponse response = await client.ListBuckets(
                        new ListBucketsRequest { NamespaceName = space, CompartmentId = factory.CompartmentId },
                        cancellationToken: ct).ConfigureAwait(false);
                    IEnumerable<string> names = response.Items.Select(b => $"  {b.Name}");

                    return $"{response.Items.Count} bucket(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateBucket",
                $"POST {origin}/n/{space}/b/\nContent-Type: application/json\n\n{{ \"name\": \"{bucket}\", \"compartmentId\": \"{factory.CompartmentId}\" }}",
                async () =>
                {
                    // Set before the call, not after: if the POST lands but the response does not
                    // come back, the bucket exists and cleanup has to know about it. Cleanup
                    // treats an absent bucket as a no-op, so claiming it early is free.
                    created = true;
                    CreateBucketResponse response = await client.CreateBucket(
                        new CreateBucketRequest
                        {
                            NamespaceName = space,
                            CreateBucketDetails = new CreateBucketDetails
                            {
                                Name = bucket,
                                CompartmentId = factory.CompartmentId,
                            },
                        },
                        cancellationToken: ct).ConfigureAwait(false);

                    return $"Location: {response.Location}\n"
                        + $"ETag:     {response.ETag}\n\n"
                        + $"Bucket {response.Bucket.Name}\n"
                        + $"  id:           {response.Bucket.Id}\n"
                        + $"  storageTier:  {response.Bucket.StorageTier}\n"
                        + $"  publicAccess: {response.Bucket.PublicAccessType}\n"
                        + $"  versioning:   {response.Bucket.Versioning}\n"
                        + $"  timeCreated:  {response.Bucket.TimeCreated:O}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutObject",
                // One PUT of the bytes, the same shape S3 and Blob use and the opposite of GCS's
                // two-request resumable upload. The slash in the name is escaped because OCI has
                // no directories — "hello/" is part of the name, not a path segment.
                $"PUT {origin}/n/{space}/b/{bucket}/o/{Uri.EscapeDataString(ObjectName)}\nContent-Type: {ObjectContentType}\n\n{ObjectBody}",
                async () =>
                {
                    using MemoryStream body = new(Encoding.UTF8.GetBytes(ObjectBody));
                    PutObjectResponse response = await client.PutObject(
                        new PutObjectRequest
                        {
                            NamespaceName = space,
                            BucketName = bucket,
                            ObjectName = ObjectName,
                            ContentType = ObjectContentType,
                            PutObjectBody = body,
                        },
                        cancellationToken: ct).ConfigureAwait(false);

                    return $"ETag:            {response.ETag}\n"
                        + $"opc-content-md5: {response.OpcContentMd5}\n"
                        + $"last-modified:   {response.LastModified:O}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListObjects",
                $"GET {origin}/n/{space}/b/{bucket}/o\nobjectStorage.ListObjects(new ListObjectsRequest {{ NamespaceName, BucketName }})",
                async () =>
                {
                    ListObjectsResponse response = await client.ListObjects(
                        new ListObjectsRequest
                        {
                            NamespaceName = space,
                            BucketName = bucket,
                            // Required against real OCI, which returns *only* the name unless the
                            // extra fields are asked for by name. floci-oci 0.3.0 ignores the
                            // parameter and always sends the full summary, so leaving this out
                            // renders correctly on the emulator and blank in production — the
                            // exact divergence this repo exists to catch. Verified by curl
                            // against floci-oci 0.3.0, 2026-08-29.
                            Fields = "name,size,md5,timeCreated",
                        },
                        cancellationToken: ct).ConfigureAwait(false);
                    IEnumerable<string> names = response.ListObjects.Objects
                        .Select(o => $"  {o.Name} ({o.Size} bytes, md5 {o.Md5})");

                    return $"{response.ListObjects.Objects.Count} object(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetObject",
                $"GET {origin}/n/{space}/b/{bucket}/o/{Uri.EscapeDataString(ObjectName)}\nobjectStorage.GetObject(new GetObjectRequest {{ NamespaceName, BucketName, ObjectName }})",
                async () =>
                {
                    GetObjectResponse response = await client.GetObject(
                        new GetObjectRequest { NamespaceName = space, BucketName = bucket, ObjectName = ObjectName },
                        cancellationToken: ct).ConfigureAwait(false);

                    // The SDK hands back the response stream, so the caller owns disposing it —
                    // unlike GCS, which writes into a stream you supply.
                    using StreamReader reader = new(response.InputStream);

                    return $"Content-Type:   {response.ContentType}\n"
                        + $"Content-Length: {response.ContentLength}\n"
                        + $"ETag:           {response.ETag}\n"
                        + $"storage-tier:   {response.StorageTier}\n\n"
                        + await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteObject",
                $"DELETE {origin}/n/{space}/b/{bucket}/o/{Uri.EscapeDataString(ObjectName)}\nobjectStorage.DeleteObject(new DeleteObjectRequest {{ NamespaceName, BucketName, ObjectName }})",
                async () =>
                {
                    DeleteObjectResponse response = await client.DeleteObject(
                        new DeleteObjectRequest { NamespaceName = space, BucketName = bucket, ObjectName = ObjectName },
                        cancellationToken: ct).ConfigureAwait(false);

                    return $"204 No Content — the object is gone.\nopc-request-id: {response.OpcRequestId}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean compartment. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created && space is not null
                ? await DeleteBucketAsync(client, origin, space, bucket, ct).ConfigureAwait(false)
                : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> handles the transport cases but cannot see a status
    /// code hiding inside an <see cref="OciException"/>, which is where this SDK puts every answer
    /// the server gave. A refused connection is the other shape, and it arrives unwrapped: a plain
    /// <see cref="HttpRequestException"/> with no status and a <see cref="SocketException"/> under
    /// it, because the OCI stack only builds an <see cref="OciException"/> once it has a response
    /// to describe.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OciException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case OciException:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real OCI would not.
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

    /// <summary>
    /// An <see cref="OciException"/>'s message is a ten-line support essay — endpoint, timestamp,
    /// client version, two documentation links. The first line carries the whole story, and it is
    /// the only part that fits in a probe result or a coverage-matrix cell.
    /// </summary>
    private static string Describe(Exception ex)
    {
        string message = ex is OciException oci
            ? $"{(int)oci.StatusCode} {oci.ServiceCode}: {FirstLine(oci.Message)}"
            : ex.Message;

        return ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? message
            : $"{message} ({FirstLine(ex.InnerException.Message)})";
    }

    private static string FirstLine(string message)
        => message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? message;

    /// <summary>
    /// Cleanup. OCI agrees with S3 and GCS and disagrees with Azure: deleting a bucket that still
    /// holds objects is a 409 <c>Conflict</c>, so every object goes first. The calls use
    /// <see cref="CancellationToken.None"/> — a run that was cancelled still has a bucket to
    /// remove.
    ///
    /// <para>
    /// floci-oci 0.3.0 enforces that rule, which makes it the only one of the four emulators whose
    /// delete semantics are known to match its own cloud: floci-gcp answers 204 to a non-empty
    /// bucket delete and orphans the objects. Verified by hand 2026-08-29.
    /// </para>
    /// </summary>
    private static async Task<DemoStep> DeleteBucketAsync(ObjectStorageClient client, string origin, string space, string bucket, CancellationToken ct)
    {
        string request = $"DELETE {origin}/n/{space}/b/{bucket}\nobjectStorage.DeleteBucket(new DeleteBucketRequest {{ NamespaceName, BucketName }})";

        return await RunStepAsync("DeleteBucket — cleanup", request, async () =>
        {
            int removed;

            try
            {
                removed = await DrainAsync(client, space, bucket).ConfigureAwait(false);
            }
            // CreateBucket claims the name before it calls, so the bucket may never have been
            // made — that is a clean run to finish, not a cleanup failure worth showing in red.
            catch (OciException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return "Nothing to remove — the bucket was never created.";
            }

            await client.DeleteBucket(
                new DeleteBucketRequest { NamespaceName = space, BucketName = bucket },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return $"204 No Content — removed {removed} object(s) and the bucket"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every object in the bucket, a page at a time. <c>ListObjects</c> pages with
    /// <c>NextStartWith</c> rather than a token, and because each pass deletes exactly what it
    /// just listed, restarting from the top is both correct and simpler than carrying the cursor.
    ///
    /// <para>
    /// Bounded rather than <c>while (true)</c>: the loop's exit depends on the server actually
    /// removing what it said it removed, and an emulator answering 204 without deleting is
    /// precisely the divergence this repo keeps finding (see plan §14 on floci-gcp). Unbounded,
    /// that would hang the demo's cleanup — inside a <c>finally</c>, on a Blazor circuit — with no
    /// way to tell what went wrong. Failing loudly after a pass that removed nothing is better.
    /// </para>
    /// </summary>
    private static async Task<int> DrainAsync(ObjectStorageClient client, string space, string bucket)
    {
        int removed = 0;
        int previouslyListed = int.MaxValue;

        while (true)
        {
            ListObjectsResponse listed = await client.ListObjects(
                new ListObjectsRequest { NamespaceName = space, BucketName = bucket },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            int stillThere = listed.ListObjects.Objects.Count;

            if (stillThere == 0)
            {
                return removed;
            }

            // Every object listed on the previous pass was deleted and every delete reported
            // success, so this pass has to be strictly shorter. It is not, which means the service
            // is accepting DeleteObject without honouring it — loop again and this never ends.
            if (stillThere >= previouslyListed)
            {
                throw new InvalidOperationException(
                    $"Bucket {bucket} still lists {stillThere} object(s) after a pass that deleted "
                    + $"{previouslyListed}. The service is accepting DeleteObject without removing.");
            }

            previouslyListed = stillThere;

            foreach (ObjectSummary stored in listed.ListObjects.Objects)
            {
                await client.DeleteObject(
                    new DeleteObjectRequest { NamespaceName = space, BucketName = bucket, ObjectName = stored.Name },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                removed++;
            }
        }
    }
}
