using Azure.Security.KeyVault.Secrets;
using FlociLab.Core.Endpoints;

namespace FlociLab.Azure.KeyVaultSecrets;

/// <summary>
/// The whole of the emulator-specific wiring for this sample. Key Vault is a data plane
/// (docs/BLAZOR-PLAN.md §7) — a URI in the client constructor — but unlike Cosmos its routes carry
/// no vault-name segment: probing the running emulator directly shows every Key Vault path
/// (<c>GET /secrets</c>, <c>PUT /secrets/{name}</c>, ...) served at the root of the port Blob,
/// Queue and Cosmos share, so <see cref="VaultUri"/> is built from
/// <see cref="AzureEndpoints.BaseUri"/> rather than <see cref="AzureEndpoints.DataPlaneUri"/>.
///
/// Unlike Blob, Queue and Cosmos, Key Vault genuinely authenticates with a <c>TokenCredential</c>
/// rather than an account key — confirmed by probing: every route answers 401 with a
/// <c>WWW-Authenticate: Bearer</c> challenge until a real bearer token from floci-az's IMDS
/// endpoint is attached. This is the first Azure sample to reference
/// <c>FlociLab.Azure.Endpoints</c> for its <c>Credential()</c> extension, which hands back a
/// <c>ManagedIdentityCredential</c> against that same, JWKS-verifiable IMDS endpoint.
/// </summary>
public sealed class KeyVaultSecretsClientFactory(AzureEndpoints endpoints)
{
    /// <summary>Vault endpoint, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => this.VaultUri.ToString().TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci-az or real Azure.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    private Uri VaultUri => endpoints.UseEmulator
        ? endpoints.BaseUri
        : endpoints.RealCloudKeyVaultUri
            ?? throw new InvalidOperationException(
                "Floci:Azure:UseEmulator is false but no Floci:Azure:KeyVaultUri was configured. "
                + "Supply a real Key Vault URI (e.g. https://my-vault.vault.azure.net/) through user "
                + "secrets or an environment variable — never appsettings.json.");

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public SecretClient Create()
    {
        SecretClientOptions options = new();

        // Turned off for the same reason every other factory in this repo turns retries off: a
        // page whose whole job is to show "the emulator is down" has to say so quickly, and the
        // request shown beside each step is meant to be *the* request that went out.
        options.Retry.MaxRetries = 0;

        if (endpoints.UseEmulator)
        {
            // floci-az serves Key Vault over plain HTTP, and the SDK's own auth policy refuses to
            // attach a bearer token to anything but https with no override — see
            // FlociAzureExtensions.AllowInsecureBearerToken for the full story and why this is safe.
            options.AllowInsecureBearerToken(this.VaultUri);

            // floci-az's IMDS token names its resource "https://vault.azure.net" (the real Azure
            // audience — see FlociAzureExtensions), but the challenge-based auth policy also checks
            // that resource against the *requested* host and rejects 127.0.0.1 as a mismatch. Real
            // Key Vault never triggers this: the vault host and the token audience agree there.
            options.DisableChallengeResourceVerification = true;
        }

        return new SecretClient(this.VaultUri, endpoints.Credential(), options);
    }
}
