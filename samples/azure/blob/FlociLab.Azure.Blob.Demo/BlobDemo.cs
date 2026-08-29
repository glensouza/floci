using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlociLab.Core;

namespace FlociLab.Azure.Blob;

/// <summary>
/// Azure Blob Storage against floci-az. Ordinary Azure.Storage.Blobs code — the only
/// emulator-aware line in the sample is in <see cref="BlobClientFactory"/>.
/// </summary>
public sealed class BlobDemo(BlobClientFactory factory) : IServiceDemo
{
    private const string BlobName = "hello/floci.txt";
    private const string BlobBody = "Hello from FlociLab.";

    public string Provider => CloudProvider.Azure;

    public string Slug => "blob";

    public string DisplayName => "Blob Storage";

    public string Category => "Storage";

    public string Route => "/azure/blob";

    /// <summary>
    /// ListContainers, the direct analog of S3's ListBuckets: one request, no state. Only the
    /// first page is pulled — the probe is meant to be the cheapest call the service offers, and
    /// enumerating an account with a thousand containers is not that.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            BlobServiceClient client = factory.Create();
            int count = 0;

            await foreach (Page<BlobContainerItem> page in
                client.GetBlobContainersAsync(cancellationToken: ct).AsPages(pageSizeHint: 100).ConfigureAwait(false))
            {
                count = page.Values.Count;
                break;
            }

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListContainers returned {count} container(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Building the client can itself fail — a misconfigured endpoint host is rejected while
        // the connection string is parsed, before any request goes out. That has to become a
        // failed step like any other: an iterator that throws on the first MoveNextAsync takes
        // down the circuit instead of rendering the reason. The exception is caught here and
        // yielded below, because C# forbids a yield inside a try that has a catch.
        BlobServiceClient? client = null;
        Exception? clientFailure = null;

        try
        {
            client = factory.Create();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clientFailure = ex;
        }

        if (client is null)
        {
            yield return DemoStep.Failed("BlobServiceClient", clientFailure!, "new BlobServiceClient(connectionString)");

            yield break;
        }

        // Unique per run, so two runs never collide and a leftover container from a crashed run
        // never makes the next one fail. 24 chars, inside Azure's 3-63 lowercase-and-hyphens rule.
        string containerName = $"flocilab-blob-{Guid.NewGuid():N}"[..24];
        BlobContainerClient container = client.GetBlobContainerClient(containerName);
        BlobClient blob = container.GetBlobClient(BlobName);
        bool created = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListContainers — before",
                $"GET {factory.ServiceUrl}?comp=list\nblobService.GetBlobContainersAsync()",
                async () =>
                {
                    List<string> names = [];

                    await foreach (BlobContainerItem item in client.GetBlobContainersAsync(cancellationToken: ct).ConfigureAwait(false))
                    {
                        names.Add($"  {item.Name}");
                    }

                    return $"{names.Count} container(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateContainer",
                $"PUT {factory.ServiceUrl}/{containerName}?restype=container\ncontainer.CreateAsync()",
                async () =>
                {
                    // Set before the call, not after: if the PUT lands but the response does not
                    // come back, the container exists and cleanup has to know about it. Cleanup
                    // treats an absent container as a no-op, so claiming it early is free.
                    created = true;
                    Response<BlobContainerInfo> response = await container.CreateAsync(cancellationToken: ct).ConfigureAwait(false);

                    return $"HTTP {response.GetRawResponse().Status} — ETag: {response.Value.ETag}\n"
                        + $"x-ms-request-id: {RequestIdOf(response.GetRawResponse())}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UploadBlob",
                $"PUT {factory.ServiceUrl}/{containerName}/{BlobName}\nx-ms-blob-type: BlockBlob\nContent-Type: text/plain\n\n{BlobBody}",
                async () =>
                {
                    Response<BlobContentInfo> response = await blob.UploadAsync(
                        BinaryData.FromString(BlobBody),
                        new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "text/plain" } },
                        ct).ConfigureAwait(false);

                    return $"HTTP {response.GetRawResponse().Status} — ETag: {response.Value.ETag}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListBlobs",
                $"GET {factory.ServiceUrl}/{containerName}?restype=container&comp=list\ncontainer.GetBlobsAsync()",
                async () =>
                {
                    List<string> lines = [];

                    await foreach (BlobItem item in container.GetBlobsAsync(cancellationToken: ct).ConfigureAwait(false))
                    {
                        lines.Add($"  {item.Name} ({item.Properties.ContentLength} bytes, {item.Properties.ContentType})");
                    }

                    return $"{lines.Count} blob(s)\n" + string.Join('\n', lines);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DownloadBlob",
                $"GET {factory.ServiceUrl}/{containerName}/{BlobName}\nblob.DownloadContentAsync()",
                async () =>
                {
                    Response<BlobDownloadResult> response = await blob.DownloadContentAsync(ct).ConfigureAwait(false);

                    return $"HTTP {response.GetRawResponse().Status} — Content-Type: {response.Value.Details.ContentType}\n\n"
                        + response.Value.Content;
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteBlob",
                $"DELETE {factory.ServiceUrl}/{containerName}/{BlobName}\nblob.DeleteAsync()",
                async () =>
                {
                    Response response = await blob.DeleteAsync(cancellationToken: ct).ConfigureAwait(false);

                    return $"HTTP {response.Status}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await DeleteContainerAsync(container, factory.ServiceUrl, containerName, ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// Azure reports both of the interesting failures inside a <see cref="RequestFailedException"/>,
    /// so <see cref="ProbeResult.FromException"/> — which inspects only the outermost exception —
    /// cannot classify them on its own. A 501 arrives as <see cref="RequestFailedException.Status"/>;
    /// a refused connection arrives as the same exception type with a status of 0 and a transport
    /// exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RequestFailedException { Status: (int)HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case RequestFailedException { Status: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Blob Storage would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the
        // container. Catching it here would instead fabricate a "Failed" step for every remaining
        // operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// Cleanup, and the sharpest contrast with S3 on the whole page: deleting a container takes
    /// one call and takes its blobs with it, where DeleteBucket on a non-empty bucket is a 409 and
    /// every key has to go first. The call uses <see cref="CancellationToken.None"/> — a run that
    /// was cancelled still has a container to remove.
    /// </summary>
    private static async Task<DemoStep> DeleteContainerAsync(BlobContainerClient container, string serviceUrl, string containerName, CancellationToken ct)
    {
        string request = $"DELETE {serviceUrl}/{containerName}?restype=container\ncontainer.DeleteIfExistsAsync()";

        return await RunStepAsync("DeleteContainer — cleanup", request, async () =>
        {
            // DeleteIfExists rather than Delete, because CreateContainer claims the name before it
            // calls and the container may never have been made. Note that floci-az answers 202 and
            // reports deleted=true for a container that never existed, where real Azure answers
            // 404 and reports false (plan §14) — so this line is more forgiving here than in
            // production, not less.
            Response<bool> response = await container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);

            // Report what the call actually returned rather than asserting the happy path. The
            // moment floci-az starts answering 404/false like real Azure — which is the change the
            // plan §14 note exists to catch — this step has to stop claiming it deleted something.
            string outcome = response.Value
                ? "removed the container and everything in it, in one call"
                : "the container was already gone; nothing to remove";

            return $"HTTP {response.GetRawResponse().Status} — {outcome}"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    private static string RequestIdOf(Response response)
        => response.Headers.TryGetValue("x-ms-request-id", out string? id) ? id : "(none)";

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
