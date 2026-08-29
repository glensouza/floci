using System.Security.Cryptography;
using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Endpoints;

/// <summary>
/// OCI request signatures are parsed but never verified by the emulator, so the config profile
/// only has to be well-formed (docs/BLAZOR-PLAN.md §7). The signing key is generated at startup
/// and lives for the life of the process — shipping a private key in the repo would be worse in
/// every way, including as an example.
/// </summary>
public sealed class OciEndpoints(IOptions<FlociOptions> options)
{
    private readonly OciEmulatorOptions _options = options.Value.Oci;
    private readonly Lazy<OciSigningKey> _key = new(OciSigningKey.Generate, isThreadSafe: true);

    /// <summary>Passed to <c>client.SetEndpoint(...)</c> after the client is constructed.</summary>
    public string Endpoint => _options.Endpoint;

    public string Region => _options.Region;

    /// <summary>The throwaway RSA key backing the config profile.</summary>
    public OciSigningKey SigningKey => _key.Value;

    /// <summary>
    /// Configuration wins, then whatever the container was started with, then the lab default.
    /// The emulator parses the OCID but never verifies it.
    /// </summary>
    public string TenancyId => Coalesce(
        _options.TenancyId,
        Environment.GetEnvironmentVariable("FLOCI_OCI_DEFAULT_TENANCY_ID"),
        OciEmulatorOptions.DefaultTenancyId);

    public string UserId => Coalesce(_options.UserId, "ocid1.user.oc1..aaaaaaaaflocilabdefaultuser");

    private static string Coalesce(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
}

/// <summary>A generated RSA key plus the fingerprint an OCI config profile expects.</summary>
public sealed class OciSigningKey
{
    private OciSigningKey(string privateKeyPem, string fingerprint)
    {
        PrivateKeyPem = privateKeyPem;
        Fingerprint = fingerprint;
    }

    public string PrivateKeyPem { get; }

    /// <summary>Colon-separated MD5 of the public key DER — OCI's fingerprint format, not a hash for security.</summary>
    public string Fingerprint { get; }

    public static OciSigningKey Generate()
    {
        using var rsa = RSA.Create(2048);
        var der = rsa.ExportSubjectPublicKeyInfo();
        var digest = MD5.HashData(der);
        var fingerprint = Convert.ToHexStringLower(digest);
        var colonised = string.Join(':', Enumerable.Range(0, digest.Length)
            .Select(i => fingerprint.Substring(i * 2, 2)));

        return new OciSigningKey(rsa.ExportPkcs8PrivateKeyPem(), colonised);
    }
}
