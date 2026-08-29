namespace FlociLab.Core.Capabilities;

/// <summary>DynamoDB · Cosmos NoSQL · Firestore. No OCI analog, by design.</summary>
public interface IDocumentDbCapability : ICloudCapability
{
    /// <summary>Table / container / collection, depending on the provider's vocabulary.</summary>
    Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct);

    Task CreateCollectionAsync(string name, CancellationToken ct);

    /// <summary><paramref name="json"/> is the whole document, id included.</summary>
    Task UpsertDocumentAsync(string collection, string id, string json, CancellationToken ct);

    Task<string?> GetDocumentAsync(string collection, string id, CancellationToken ct);

    Task DeleteCollectionAsync(string name, CancellationToken ct);
}

public sealed record CollectionInfo(string Name, long? ItemCount = null);
