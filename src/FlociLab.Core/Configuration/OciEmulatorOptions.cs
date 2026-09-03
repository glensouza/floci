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

    /// <summary>
    /// The OCID of the vault secrets are created in, and the master encryption key that encrypts
    /// them. <c>CreateSecret</c> hard-requires both (floci-oci 400s <c>MissingParameter</c> on
    /// either being absent), and provisioning them needs <c>OCI.DotNetSDK.Keymanagement</c> — a
    /// third cloud package the Secrets sample deliberately does not carry (plan §14).
    /// So they arrive as configuration, which is also how production reaches them: a vault and key
    /// are long-lived infrastructure, provisioned once by Terraform, not created per deployment.
    ///
    /// <para>
    /// Unset by default, because floci-oci mints these OCIDs when the vault and key are created and
    /// so the lab cannot know them in advance. The OCI Vault page creates both — run it once, then
    /// set <c>Floci:Oci:VaultId</c> and <c>Floci:Oci:KeyId</c> from the OCIDs it prints.
    /// </para>
    /// </summary>
    public string? VaultId { get; set; }

    /// <inheritdoc cref="VaultId"/>
    public string? KeyId { get; set; }
}
