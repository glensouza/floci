using System.Text.Json;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Google.Cloud.Firestore;

namespace FlociLab.Gcp.Firestore;

/// <summary>
/// The document-DB column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto Google.Cloud.Firestore: the comparison is only worth anything if
/// each column is the provider's own SDK doing the provider's own thing.
///
/// <para>
/// Firestore has no create-collection or delete-collection RPC — see <see cref="FirestoreDemo"/>'s
/// remarks. <see cref="CreateCollectionAsync"/> writes a placeholder document so the collection
/// exists the moment this method returns, matching the other three columns'
/// <c>CreateCollectionAsync</c> contract; <see cref="DeleteCollectionAsync"/> removes every
/// document the collection holds, placeholder included, which is the only way to make it stop
/// appearing in <see cref="ListCollectionsAsync"/>.
/// </para>
/// </summary>
public sealed class FirestoreDocumentDb(FirestoreClientFactory factory) : IDocumentDbCapability
{
    private const string PlaceholderDocumentId = "_flocilab-placeholder";

    public string Provider => CloudProvider.Gcp;

    public string ServiceName => "Google Cloud Firestore";

    // The same classifier FirestoreDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => FirestoreDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct)
    {
        FirestoreDb db = factory.Create();
        List<CollectionInfo> collections = [];

        await foreach (CollectionReference collection in db.ListRootCollectionsAsync().WithCancellation(ct).ConfigureAwait(false))
        {
            collections.Add(new CollectionInfo(collection.Id));
        }

        return collections;
    }

    public async Task CreateCollectionAsync(string name, CancellationToken ct)
    {
        await factory.Create().Collection(name).Document(PlaceholderDocumentId)
            .SetAsync(new Dictionary<string, object> { ["id"] = PlaceholderDocumentId }, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The document's own "id" field is overwritten with <paramref name="id"/> rather than trusted
    /// as-is, matching DynamoDbDocumentDb and CosmosDbDocumentDb: it has to be the value
    /// <see cref="GetDocumentAsync"/> will look up by, not whatever the caller's JSON carried.
    /// </summary>
    public async Task UpsertDocumentAsync(string collection, string id, string json, CancellationToken ct)
    {
        Dictionary<string, object?> fields = ParseFields(json);
        fields["id"] = id;

        await factory.Create().Collection(collection).Document(id).SetAsync(fields, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<string?> GetDocumentAsync(string collection, string id, CancellationToken ct)
    {
        DocumentSnapshot snapshot = await factory.Create().Collection(collection).Document(id).GetSnapshotAsync(ct).ConfigureAwait(false);

        return snapshot.Exists ? JsonSerializer.Serialize(snapshot.ToDictionary()) : null;
    }

    public async Task DeleteCollectionAsync(string name, CancellationToken ct)
    {
        FirestoreDb db = factory.Create();
        CollectionReference collectionRef = db.Collection(name);

        await foreach (DocumentReference document in collectionRef.ListDocumentsAsync().WithCancellation(ct).ConfigureAwait(false))
        {
            await document.DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Firestore's <see cref="DocumentReference.SetAsync"/> serializes a
    /// <c>Dictionary&lt;string, object&gt;</c> natively, so this walks the JSON tree into exactly
    /// that shape rather than reaching for a converter attribute the way an attributed POCO would.
    /// </summary>
    private static Dictionary<string, object?> ParseFields(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        return parsed.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => ToFirestoreValue(p.Value));
    }

    private static object? ToFirestoreValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToFirestoreValue(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToFirestoreValue).ToList(),
        _ => throw new NotSupportedException($"Unsupported JSON value kind: {element.ValueKind}"),
    };
}
