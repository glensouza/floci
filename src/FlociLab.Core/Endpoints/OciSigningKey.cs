using System.Security.Cryptography;

namespace FlociLab.Core.Endpoints;

/// <summary>A generated RSA key plus the fingerprint an OCI config profile expects.</summary>
public sealed class OciSigningKey
{
    private OciSigningKey(string privateKeyPem, string fingerprint)
    {
        this.PrivateKeyPem = privateKeyPem;
        this.Fingerprint = fingerprint;
    }

    public string PrivateKeyPem { get; }

    /// <summary>Colon-separated MD5 of the public key DER — OCI's fingerprint format, not a hash for security.</summary>
    public string Fingerprint { get; }

    public static OciSigningKey Generate()
    {
        using RSA rsa = RSA.Create(2048);
        byte[] der = rsa.ExportSubjectPublicKeyInfo();
        byte[] digest = MD5.HashData(der);
        string fingerprint = Convert.ToHexStringLower(digest);
        string colonised = string.Join(':', Enumerable.Range(0, digest.Length)
            .Select(i => fingerprint.Substring(i * 2, 2)));

        return new OciSigningKey(rsa.ExportPkcs8PrivateKeyPem(), colonised);
    }
}
