namespace FlociLab.Core.Capabilities;

/// <summary>S3 · Blob · GCS · OCI Object Storage.</summary>
public interface IObjectStoreCapability : ICloudCapability
{
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct);

    Task CreateContainerAsync(string name, CancellationToken ct);

    Task PutObjectAsync(string container, string key, Stream data, CancellationToken ct);

    Task<Stream> GetObjectAsync(string container, string key, CancellationToken ct);

    Task DeleteContainerAsync(string name, CancellationToken ct);
}
