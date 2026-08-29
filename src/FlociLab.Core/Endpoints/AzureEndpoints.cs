using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Endpoints;

/// <summary>
/// Azure has no single endpoint knob — it has three planes, and a sample picks the one its SDK
/// uses (docs/BLAZOR-PLAN.md §7):
///
/// <list type="bullet">
///   <item>Storage (Blob/Queue/Table) — <see cref="StorageConnectionString"/>.</item>
///   <item>ARM (VM, VNet, AKS, ACR, Redis, ACI, Event Grid, Monitor) —
///         <see cref="ArmUri"/> via <c>ArmClientOptions.Environment</c>.</item>
///   <item>Data plane (Key Vault, App Configuration, Cosmos, Service Bus, Event Hubs) — a URI in
///         the client constructor, built with <see cref="DataPlaneUri"/>.</item>
/// </list>
///
/// Credentials are real: floci-az signs verifiable v1.0 JWTs from an IMDS endpoint, so samples use
/// <c>ManagedIdentityCredential</c> against <see cref="ImdsAuthorityHost"/> rather than a
/// hand-rolled fake <c>TokenCredential</c>.
/// </summary>
public sealed class AzureEndpoints(IOptions<FlociOptions> options)
{
    private readonly AzureEmulatorOptions emulatorOptions = options.Value.Azure;

    public Uri BaseUri => new(this.emulatorOptions.Endpoint);

    public string AccountName => this.emulatorOptions.AccountName;

    /// <summary>ARM plane: <c>new ArmClientOptions { Environment = new ArmEnvironment(endpoints.ArmUri, ...) }</c>.</summary>
    public Uri ArmUri => this.BaseUri;

    /// <summary>
    /// Set as AZURE_POD_IDENTITY_AUTHORITY_HOST so <c>ManagedIdentityCredential</c> talks to the
    /// emulator's IMDS endpoint. A host, not a URL: Azure.Identity appends
    /// /metadata/identity/oauth2/token itself. Plan §7 shows the full-URL form, which was the old
    /// AZURE_POD_IDENTITY_TOKEN_URL variable and no longer works.
    /// </summary>
    public string ImdsAuthorityHost => this.emulatorOptions.Endpoint.TrimEnd('/');

    /// <summary>The full token URL, for probing the endpoint by hand or from a raw HttpClient.</summary>
    public Uri ImdsTokenUri => new(this.Combine("metadata/identity/oauth2/token"));

    /// <summary>
    /// AMQP 1.0 host for Service Bus / Event Hubs. These do NOT go over the HTTP port —
    /// clients need <c>ServiceBusTransportType.AmqpTcp</c> against this host and port.
    /// </summary>
    public string AmqpHost => this.BaseUri.Host;

    public int ServiceBusAmqpPort => this.emulatorOptions.ServiceBusAmqpPort;

    public int EventHubsAmqpPort => this.emulatorOptions.EventHubsAmqpPort;

    /// <summary>
    /// Composes a data-plane URI from a relative path. The path is deliberately the caller's
    /// business: each service's shape is whatever floci-az actually serves, which the sample
    /// confirms against the running emulator rather than assuming.
    /// </summary>
    public Uri DataPlaneUri(string relativePath) => new(this.Combine(relativePath));

    /// <summary>
    /// Storage connection string with explicit per-service endpoints — the emulator serves all
    /// three from one port, so the SDK cannot infer them from the account name.
    /// </summary>
    public string StorageConnectionString(string? accountName = null)
    {
        string account = accountName ?? this.AccountName;
        string scheme = this.BaseUri.Scheme;
        string root = this.emulatorOptions.Endpoint.TrimEnd('/');

        return $"DefaultEndpointsProtocol={scheme};" +
               $"AccountName={account};" +
               $"AccountKey={this.emulatorOptions.AccountKey};" +
               $"BlobEndpoint={root}/{account};" +
               $"QueueEndpoint={root}/{account};" +
               $"TableEndpoint={root}/{account};";
    }

    private string Combine(string relativePath)
        => $"{this.emulatorOptions.Endpoint.TrimEnd('/')}/{relativePath.TrimStart('/')}";
}
