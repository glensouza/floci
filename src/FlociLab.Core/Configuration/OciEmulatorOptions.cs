namespace FlociLab.Core.Configuration;

public sealed class OciEmulatorOptions : EmulatorOptions
{
    /// <summary>
    /// The emulator does not issue a tenancy OCID of its own and does not verify the one it is
    /// given — checked against a running floci-oci 0.3.0, which sets no FLOCI_OCI_DEFAULT_TENANCY_ID
    /// unless you do. So the lab picks one, the AppHost passes the same value to the container, and
    /// everything lines up. Well-formed, obviously synthetic, and not a secret.
    /// </summary>
    public const string DefaultTenancyId = "ocid1.tenancy.oc1..aaaaaaaaflocilabdefaulttenancy";

    public OciEmulatorOptions()
    {
        this.Endpoint = "http://127.0.0.1:4599";
        this.HealthPath = "/_floci-oci/health";
    }

    public string Region { get; set; } = "us-ashburn-1";

    /// <summary>Overrides <see cref="DefaultTenancyId"/> and FLOCI_OCI_DEFAULT_TENANCY_ID.</summary>
    public string? TenancyId { get; set; }

    public string? UserId { get; set; }
}
