using FlociLab.Core.Endpoints;
using Oci.Common;
using Oci.Common.Auth;

namespace FlociLab.Oci;

/// <summary>
/// The signing half of plan §7. The emulator parses request signatures but never verifies them,
/// so the profile only has to be well-formed — which means a sample can be handed a real
/// <see cref="IBasicAuthenticationDetailsProvider"/> built from a key generated at startup, with
/// no key material in the repo and no config file on disk.
///
/// <code>
/// var client = new ObjectStorageClient(endpoints.AuthenticationProvider());
/// client.SetEndpoint(endpoints.Endpoint);
/// </code>
/// </summary>
public static class FlociOciExtensions
{
    public static IBasicAuthenticationDetailsProvider AuthenticationProvider(this OciEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return new SimpleAuthenticationDetailsProvider
        {
            TenantId = endpoints.TenancyId,
            UserId = endpoints.UserId,
            Fingerprint = endpoints.SigningKey.Fingerprint,
            // Takes the PEM itself, not a path — verified against 145.0.0, which parses
            // ExportPkcs8PrivateKeyPem output into BouncyCastle RsaKeyParameters.
            PrivateKeySupplier = new PrivateKeySupplier(endpoints.SigningKey.PrivateKeyPem),
            Region = Region.FromRegionCodeOrId(endpoints.Region),
        };
    }
}
