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

    /// <summary>
    /// False targets real Oracle Cloud: the factory builds its authentication provider from
    /// ~/.oci/config instead of the generated key, and leaves the endpoint alone.
    /// </summary>
    public bool UseEmulator => this.emulatorOptions.UseEmulator;

    /// <summary>
    /// Handed to <c>ForFloci(...)</c> in FlociLab.Oci.Endpoints after the client is constructed.
    /// Setting it with <c>SetEndpoint</c> alone is not enough — see that method for why.
    /// </summary>
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

    /// <summary>
    /// The tenancy OCID as configured, before the environment variable and the lab default fall
    /// back. A real-cloud guard has to read this rather than <see cref="TenancyId"/>:
    /// FLOCI_OCI_DEFAULT_TENANCY_ID is an emulator-side convenience — the AppHost and the
    /// integration tests set it to line a container up with the samples — so a value arriving
    /// from there is not evidence that anyone chose a real compartment.
    /// </summary>
    public string? ConfiguredTenancyId => this.emulatorOptions.TenancyId;

    public string UserId => Coalesce(this.emulatorOptions.UserId, "ocid1.user.oc1..aaaaaaaaflocilabdefaultuser");

    private static string Coalesce(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
}
