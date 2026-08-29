namespace FlociLab.Core.Capabilities;

/// <summary>Secrets Manager · Key Vault · Secret Manager · OCI Vault.</summary>
public interface ISecretStoreCapability : ICloudCapability
{
    Task<IReadOnlyList<SecretInfo>> ListSecretsAsync(CancellationToken ct);

    Task SetSecretAsync(string name, string value, CancellationToken ct);

    Task<string> GetSecretAsync(string name, CancellationToken ct);

    Task DeleteSecretAsync(string name, CancellationToken ct);
}
