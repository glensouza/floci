namespace FlociLab.Core.Configuration;

public sealed class AzureEmulatorOptions : EmulatorOptions
{
    public AzureEmulatorOptions() => this.Endpoint = "http://127.0.0.1:4577";

    public string AccountName { get; set; } = "devstoreaccount1";

    /// <summary>The well-known public Azurite development key. Not a secret.</summary>
    public string AccountKey { get; set; } =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>
    /// A real Azure storage connection string, used only when <see cref="EmulatorOptions.UseEmulator"/>
    /// is <c>false</c>. Azure is the one provider where real-cloud mode needs a value rather than
    /// just the absence of an override: AWS and GCP both have an ambient credential chain to fall
    /// back on, and storage is the Azure plane that authenticates with an account key instead of a
    /// <c>TokenCredential</c>. Taking a connection string keeps the sample on one package — reaching
    /// for <c>DefaultAzureCredential</c> would mean adding <c>Azure.Identity</c> and breaking
    /// constraint 1 (docs/BLAZOR-PLAN.md §3).
    ///
    /// <para><strong>This is a real secret when set.</strong> Supply it through user secrets or an
    /// environment variable, never appsettings.json, and never on camera.</para>
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Service Bus AMQP 1.0 port (README Compose stack).</summary>
    public int ServiceBusAmqpPort { get; set; } = 5673;

    /// <summary>Event Hubs AMQP 1.0 port.</summary>
    public int EventHubsAmqpPort { get; set; } = 5672;

    /// <summary>
    /// A real Cosmos DB account connection string (<c>AccountEndpoint=...;AccountKey=...;</c>), used
    /// only when <see cref="EmulatorOptions.UseEmulator"/> is <c>false</c>. Same reasoning as
    /// <see cref="ConnectionString"/>: Cosmos supports <c>DefaultAzureCredential</c>, but taking that
    /// route would mean adding <c>Azure.Identity</c> to a sample that otherwise references only
    /// <c>Microsoft.Azure.Cosmos</c>, breaking constraint 1 (docs/BLAZOR-PLAN.md §3).
    ///
    /// <para><strong>This is a real secret when set.</strong> Supply it through user secrets or an
    /// environment variable, never appsettings.json, and never on camera.</para>
    /// </summary>
    public string? CosmosConnectionString { get; set; }

    /// <summary>
    /// A real Key Vault URI (<c>https://my-vault.vault.azure.net/</c>), used only when
    /// <see cref="EmulatorOptions.UseEmulator"/> is <c>false</c>. Not a secret — Key Vault, unlike
    /// storage and Cosmos, authenticates with a <c>TokenCredential</c> rather than an account key,
    /// so the URI alone grants nothing.
    /// </summary>
    public string? KeyVaultUri { get; set; }

    /// <summary>
    /// A real Service Bus fully qualified namespace (<c>my-namespace.servicebus.windows.net</c>),
    /// used only when <see cref="EmulatorOptions.UseEmulator"/> is <c>false</c>. Not a secret — like
    /// Key Vault, Service Bus authenticates with a <c>TokenCredential</c> rather than a connection
    /// string, so the namespace alone grants nothing.
    /// </summary>
    public string? ServiceBusNamespace { get; set; }
}
