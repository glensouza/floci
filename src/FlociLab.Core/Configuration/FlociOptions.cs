namespace FlociLab.Core.Configuration;

/// <summary>
/// Bound from the "Floci" configuration section (docs/BLAZOR-PLAN.md §7). Defaults are the
/// host-side ports from the Compose stack in the README, so the app works with no configuration
/// at all; the AppHost overrides them with the live container endpoints, and running inside the
/// Compose network is a matter of setting Floci__Aws__Endpoint=http://floci:4566 and friends.
/// </summary>
public sealed class FlociOptions
{
    public const string SectionName = "Floci";

    public AwsEmulatorOptions Aws { get; set; } = new();

    public AzureEmulatorOptions Azure { get; set; } = new();

    public GcpEmulatorOptions Gcp { get; set; } = new();

    public OciEmulatorOptions Oci { get; set; } = new();

    /// <summary>How long a single probe is given before it counts as unreachable.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Slugs reach here from <see cref="IServiceDemo.Provider"/> and from route values, and the
    /// rest of the codebase matches them case-insensitively (<c>DemoCatalog.Find</c>), so this
    /// does too — a case mismatch should not be a hard throw.
    /// </summary>
    public EmulatorOptions For(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider.ToLowerInvariant() switch
        {
            CloudProvider.Aws => Aws,
            CloudProvider.Azure => Azure,
            CloudProvider.Gcp => Gcp,
            CloudProvider.Oci => Oci,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider slug."),
        };
    }
}

public abstract class EmulatorOptions
{
    /// <summary>Base URL of the emulator, e.g. http://localhost:4566.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// The image's health endpoint. NOT uniform across the four images, despite the README:
    /// floci and floci-az serve /_floci/health, while floci-gcp and floci-oci serve
    /// /_floci-gcp/health and /_floci-oci/health and return 404 on /_floci/health. Confirmed
    /// against the containers' own HEALTHCHECK commands, 2026-08-28.
    /// </summary>
    public string HealthPath { get; set; } = "/_floci/health";
}

public sealed class AwsEmulatorOptions : EmulatorOptions
{
    public AwsEmulatorOptions() => Endpoint = "http://localhost:4566";

    public string Region { get; set; } = "us-east-1";

    /// <summary>Floci parses credentials but does not verify them; "test"/"test" is the convention.</summary>
    public string AccessKeyId { get; set; } = "test";

    public string SecretAccessKey { get; set; } = "test";
}

public sealed class AzureEmulatorOptions : EmulatorOptions
{
    public AzureEmulatorOptions() => Endpoint = "http://localhost:4577";

    public string AccountName { get; set; } = "devstoreaccount1";

    /// <summary>The well-known public Azurite development key. Not a secret.</summary>
    public string AccountKey { get; set; } =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>Service Bus AMQP 1.0 port (README Compose stack).</summary>
    public int ServiceBusAmqpPort { get; set; } = 5673;

    /// <summary>Event Hubs AMQP 1.0 port.</summary>
    public int EventHubsAmqpPort { get; set; } = 5672;
}

public sealed class GcpEmulatorOptions : EmulatorOptions
{
    public GcpEmulatorOptions()
    {
        Endpoint = "http://localhost:4588";
        HealthPath = "/_floci-gcp/health";
    }

    public string ProjectId { get; set; } = "floci-local";
}

public sealed class OciEmulatorOptions : EmulatorOptions
{
    public OciEmulatorOptions()
    {
        Endpoint = "http://localhost:4599";
        HealthPath = "/_floci-oci/health";
    }

    public string Region { get; set; } = "us-ashburn-1";

    /// <summary>
    /// The emulator does not issue a tenancy OCID of its own and does not verify the one it is
    /// given — checked against a running floci-oci 0.3.0, which sets no FLOCI_OCI_DEFAULT_TENANCY_ID
    /// unless you do. So the lab picks one, the AppHost passes the same value to the container, and
    /// everything lines up. Well-formed, obviously synthetic, and not a secret.
    /// </summary>
    public const string DefaultTenancyId = "ocid1.tenancy.oc1..aaaaaaaaflocilabdefaulttenancy";

    /// <summary>Overrides <see cref="DefaultTenancyId"/> and FLOCI_OCI_DEFAULT_TENANCY_ID.</summary>
    public string? TenancyId { get; set; }

    public string? UserId { get; set; }
}
