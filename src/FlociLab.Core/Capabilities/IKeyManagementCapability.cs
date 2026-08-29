namespace FlociLab.Core.Capabilities;

/// <summary>KMS · Key Vault keys · Cloud KMS · OCI KMS.</summary>
public interface IKeyManagementCapability : ICloudCapability
{
    Task<IReadOnlyList<KeyInfo>> ListKeysAsync(CancellationToken ct);

    /// <summary>Returns the provider's identifier for the new key (ARN, key URI, OCID, ...).</summary>
    Task<string> CreateKeyAsync(string name, CancellationToken ct);

    Task<byte[]> EncryptAsync(string keyId, byte[] plaintext, CancellationToken ct);

    Task<byte[]> DecryptAsync(string keyId, byte[] ciphertext, CancellationToken ct);

    Task DeleteKeyAsync(string keyId, CancellationToken ct);
}

public sealed record KeyInfo(string Id, string? Name = null, string? Algorithm = null);
