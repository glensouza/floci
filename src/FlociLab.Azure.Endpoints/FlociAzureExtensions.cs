using Azure.Core;
using Azure.Identity;
using FlociLab.Core.Endpoints;

namespace FlociLab.Azure;

/// <summary>
/// The credential half of plan §7. floci-az implements the IMDS token endpoint and signs real
/// v1.0 JWTs verifiable via JWKS, so samples authenticate with the same
/// <see cref="ManagedIdentityCredential"/> they would use on a real VM — no hand-rolled fake
/// <see cref="TokenCredential"/> anywhere in the repo.
/// </summary>
public static class FlociAzureExtensions
{
    /// <summary>
    /// The only supported way to point Azure.Identity at a different IMDS address. It takes a
    /// host — "http://localhost:4577" — and the credential appends
    /// /metadata/identity/oauth2/token itself. (It was called AZURE_POD_IDENTITY_TOKEN_URL and did
    /// take a full URL, which is the shape plan §7 still shows.)
    /// </summary>
    public const string PodIdentityAuthorityHostVariable = "AZURE_POD_IDENTITY_AUTHORITY_HOST";

    /// <summary>
    /// A credential that gets its tokens from the emulator. Sets the environment variable if
    /// nothing has set it yet, so a standalone sample host works with no extra configuration; when
    /// the AppHost has already set it, the existing value wins.
    /// </summary>
    public static TokenCredential Credential(this AzureEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PodIdentityAuthorityHostVariable)))
        {
            Environment.SetEnvironmentVariable(PodIdentityAuthorityHostVariable, endpoints.ImdsAuthorityHost);
        }

        // The parameterless and (clientId, options) constructors are obsolete in Azure.Identity
        // 1.21; the options overload is the supported way to ask for the system-assigned identity.
        return new ManagedIdentityCredential(new ManagedIdentityCredentialOptions());
    }
}
