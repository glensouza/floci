using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FlociLab.Core;
using Google.Cloud.Firestore;
using Grpc.Core;

namespace FlociLab.Gcp.Firestore;

/// <summary>
/// Google Cloud Firestore against floci-gcp. Ordinary Google.Cloud.Firestore code — the only
/// emulator-aware lines in the sample are in <see cref="FirestoreClientFactory"/>.
///
/// <para>
/// Firestore has no create-collection or delete-collection RPC: a collection exists only for as
/// long as it holds at least one document (real Firestore behavior, not an emulator quirk — see
/// the Google Cloud docs on collections). So the round-trip below has no separate "CreateCollection"
/// step the way DynamoDB or Cosmos do — <c>SetDocument</c> is what brings the collection into
/// existence, and the cleanup step's document delete is what makes it disappear again.
/// </para>
/// </summary>
public sealed class FirestoreDemo(FirestoreClientFactory factory) : IServiceDemo
{
    private const string Greeting = "Hello from FlociLab.";

    public string Provider => CloudProvider.Gcp;

    public string Slug => "firestore";

    public string DisplayName => "Firestore";

    public string Category => "Database";

    public string Route => "/gcp/firestore";

    /// <summary>ListRootCollections — one request, no state, and the cheapest call Firestore has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            FirestoreDb db = factory.Create();
            int count = 0;

            await foreach (CollectionReference collection in db.ListRootCollectionsAsync().WithCancellation(ct).ConfigureAwait(false))
            {
                _ = collection;
                count++;
            }

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListRootCollections returned {count} collection(s).");
        }
        catch (RpcException ex) when (IsCancellation(ex, ct))
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        FirestoreDb db = factory.Create();

        // Unique per run, so two runs never collide and a leftover document from a crashed run
        // never makes the next one fail. Firestore collection and document IDs allow any non-empty
        // string bar a handful of reserved forms; a GUID-derived name avoids all of them.
        string collectionId = $"flocilab-firestore-{Guid.NewGuid():N}";
        string documentId = Guid.NewGuid().ToString("N");
        bool documentWritten = false;
        bool writeConfirmed = false;
        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListCollections — before",
                $"{factory.GrpcTarget} google.firestore.v1.Firestore/ListCollectionIds\ndb.ListRootCollectionsAsync()",
                ct,
                async () =>
                {
                    List<string> names = [];

                    await foreach (CollectionReference collection in db.ListRootCollectionsAsync().WithCancellation(ct).ConfigureAwait(false))
                    {
                        names.Add($"  {collection.Id}");
                    }

                    return $"{names.Count} collection(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "SetDocument",
                $"{factory.GrpcTarget} google.firestore.v1.Firestore/Commit\n"
                    + $"db.Collection(\"{collectionId}\").Document(\"{documentId}\").SetAsync({{ id: \"{documentId}\", greeting: \"{Greeting}\" }})",
                ct,
                async () =>
                {
                    // Set before the call, not after: if the Commit lands but the response does
                    // not come back, the document exists and cleanup has to remove it. Claiming it
                    // early costs a red cleanup step when the Commit never landed at all — which is
                    // the honest outcome, because SetDocument itself is red in that case too.
                    documentWritten = true;
                    WriteResult result = await db.Collection(collectionId).Document(documentId)
                        .SetAsync(new Dictionary<string, object> { ["id"] = documentId, ["greeting"] = Greeting }, cancellationToken: ct)
                        .ConfigureAwait(false);

                    // Distinct from documentWritten: that one says "a Commit went out, so cleanup
                    // has to try", this one says "the document demonstrably exists". Cleanup needs
                    // both — see DeleteDocumentAsync for why a delete that removed nothing is not
                    // a success.
                    writeConfirmed = true;

                    return $"UpdateTime: {result.UpdateTime}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetDocument",
                $"{factory.GrpcTarget} google.firestore.v1.Firestore/BatchGetDocuments\ndb.Collection(\"{collectionId}\").Document(\"{documentId}\").GetSnapshotAsync()",
                ct,
                async () =>
                {
                    // The request line says BatchGetDocuments rather than GetDocument because that
                    // is the RPC that goes out: GetSnapshotAsync reads a single document through
                    // the batch endpoint, and google.firestore.v1.Firestore/GetDocument is never
                    // sent. Verified in floci-gcp's gRPC access log — this page claims to show the
                    // wire, so the label has to name the method the wire actually carried.
                    DocumentSnapshot snapshot = await db.Collection(collectionId).Document(documentId).GetSnapshotAsync(ct).ConfigureAwait(false);

                    // A get that found nothing did not round-trip. The lede promises this page
                    // shows what floci-gcp actually answered, so a missing document goes out red —
                    // green steps for a run that never read its own write would be the page lying.
                    if (!snapshot.Exists)
                    {
                        throw new InvalidOperationException("The document written above did not come back.");
                    }

                    return JsonSerializer.Serialize(snapshot.ToDictionary());
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean project. Yielded below — an iterator may not
            // yield from inside a finally.
            cleanup = documentWritten
                ? await DeleteDocumentAsync(db, factory.GrpcTarget, collectionId, documentId, writeConfirmed).ConfigureAwait(false)
                : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> cannot see a gRPC status hiding inside an
    /// <see cref="RpcException"/>, which is where this SDK puts every answer the server gave. A
    /// refused connection surfaces as <see cref="StatusCode.Unavailable"/> too, so the transport
    /// case has to be told apart from the emulator genuinely answering "unavailable" — which
    /// floci-gcp does not do, so treating every Unavailable as unreachable is the honest read here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RpcException { StatusCode: StatusCode.Unimplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                // DeadlineExceeded is GAX's own per-call expiration rather than this token: the
                // emulator accepted the connection and never answered, which is the same story
                // Unavailable tells and must not read as the sample being broken.
                case RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded }:
                case SocketException or TimeoutException:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // Any other status means the emulator answered, so this is it behaving badly
                // rather than being absent. Stop unwrapping and report the error.
                case RpcException:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Whether an <see cref="RpcException"/> is this token being cancelled rather than the server
    /// answering. Only a token already cancelled when the call starts throws
    /// <see cref="OperationCanceledException"/>; one that trips mid-flight surfaces as
    /// <see cref="StatusCode.Cancelled"/> instead, because the SDK reports it the way the wire
    /// carried it. Same reasoning as <c>PubSubDemo.IsCancellation</c>.
    /// </summary>
    private static bool IsCancellation(RpcException ex, CancellationToken ct)
        => ct.IsCancellationRequested && ex.StatusCode == StatusCode.Cancelled;

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Firestore would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, CancellationToken ct, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        catch (RpcException ex) when (IsCancellation(ex, ct))
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the document.
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
    /// Cleanup, and a step like any other: it goes green only when it actually removed the
    /// document. Uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// document to remove. Deleting it is also what makes the collection stop appearing in
    /// <c>ListRootCollectionsAsync</c>, since Firestore has no separate delete-collection call.
    ///
    /// <para>
    /// <see cref="Precondition.MustExist"/> is load-bearing, not belt-and-braces. A Firestore
    /// delete is idempotent: removing a document that was never written succeeds, so a plain
    /// <c>DeleteAsync</c> here would render "Removed the document." in green after a
    /// <c>SetDocument</c> that failed outright — verified against floci-gcp, which answers that
    /// delete with a perfectly successful Commit. The precondition turns it into the
    /// <c>NotFound</c> it should have been ("No document to update"). See docs/BLAZOR-PLAN.md §14
    /// on cleanup steps that render green having achieved nothing.
    /// </para>
    /// </summary>
    private static async Task<DemoStep> DeleteDocumentAsync(FirestoreDb db, string grpcTarget, string collectionId, string documentId, bool writeConfirmed)
    {
        string request = $"{grpcTarget} google.firestore.v1.Firestore/Commit (delete, currentDocument.exists=true)\ndb.Collection(\"{collectionId}\").Document(\"{documentId}\").DeleteAsync(Precondition.MustExist)";

        return await RunStepAsync("DeleteDocument — cleanup", request, CancellationToken.None, async () =>
        {
            try
            {
                await db.Collection(collectionId).Document(documentId)
                    .DeleteAsync(Precondition.MustExist, CancellationToken.None).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.FailedPrecondition)
            {
                throw new InvalidOperationException(
                    "Nothing was removed: " + (writeConfirmed
                        ? $"'{documentId}' was written by this run but is already gone, so something else deleted it."
                        : $"'{documentId}' never existed, because SetDocument above did not succeed."),
                    ex);
            }

            return "Removed the document.";
        }).ConfigureAwait(false);
    }
}
