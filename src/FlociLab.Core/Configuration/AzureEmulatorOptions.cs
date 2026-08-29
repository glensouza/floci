namespace FlociLab.Core.Configuration;

public sealed class AzureEmulatorOptions : EmulatorOptions
{
    public AzureEmulatorOptions() => this.Endpoint = "http://localhost:4577";

    public string AccountName { get; set; } = "devstoreaccount1";

    /// <summary>The well-known public Azurite development key. Not a secret.</summary>
    public string AccountKey { get; set; } =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>Service Bus AMQP 1.0 port (README Compose stack).</summary>
    public int ServiceBusAmqpPort { get; set; } = 5673;

    /// <summary>Event Hubs AMQP 1.0 port.</summary>
    public int EventHubsAmqpPort { get; set; } = 5672;
}
