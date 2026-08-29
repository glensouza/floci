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
    private readonly OciEmulatorOptions emulatorOptions = options.Value.Oci;
    private readonly Lazy<OciSigningKey> key = new(OciSigningKey.Generate, isThreadSafe: true);

    /// <summary>Passed to <c>client.SetEndpoint(...)</c> after the client is constructed.</summary>
    public string Endpoint => this.emulatorOptions.Endpoint;

    public string Region => this.emulatorOptions.Region;

    /// <summary>The throwaway RSA key backing the config profile.</summary>
    public OciSigningKey SigningKey => this.key.Value;

    /// <summary>
    /// Configuration wins, then whatever the container was started with, then the lab default.
    /// The emulator parses the OCID but never verifies it.
    /// </summary>
    public string TenancyId => Coalesce(
        this.emulatorOptions.TenancyId,
        Environment.GetEnvironmentVariable("FLOCI_OCI_DEFAULT_TENANCY_ID"),
        OciEmulatorOptions.DefaultTenancyId);

    public string UserId => Coalesce(this.emulatorOptions.UserId, "ocid1.user.oc1..aaaaaaaaflocilabdefaultuser");

    private static string Coalesce(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
}
