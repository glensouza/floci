using System.Net;
using System.Net.Sockets;
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

    private string? storageRoot;

    public Uri BaseUri => new(this.emulatorOptions.Endpoint);

    /// <summary>
    /// False targets real Azure via <see cref="RealCloudConnectionString"/>, skipping both the
    /// emulator connection string and the IPv4-literal host rewrite it needs.
    /// </summary>
    public bool UseEmulator => this.emulatorOptions.UseEmulator;

    /// <summary>The real Azure storage connection string, when configured. A secret.</summary>
    public string? RealCloudConnectionString => this.emulatorOptions.ConnectionString;

    /// <summary>The real Cosmos DB account connection string, when configured. A secret.</summary>
    public string? RealCloudCosmosConnectionString => this.emulatorOptions.CosmosConnectionString;

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
    /// The storage endpoint with its host rewritten to a literal IPv4 address — see
    /// <see cref="StorageConnectionString"/> for why that is not cosmetic. Scheme, port and
    /// nothing else: the account name is appended per service.
    /// </summary>
    public string StorageRoot
    {
        get
        {
            // Remembered so the DNS lookup below happens once rather than once per client, but
            // deliberately not under a lock: the value is deterministic, a reference assignment is
            // atomic, and the worst a race can do is resolve the same name twice. Holding a lock
            // across a blocking resolve would instead serialise every Blazor circuit rendering the
            // endpoint behind one OS resolver timeout.
            string? cached = Volatile.Read(ref this.storageRoot);

            if (cached is not null)
            {
                return cached;
            }

            (string root, bool resolved) = BuildStorageRoot(this.BaseUri);

            // Only a resolved answer is cached. A name that failed to resolve is very often
            // transient (the emulator container not up yet), and caching that would poison this
            // singleton for the lifetime of the process.
            if (resolved)
            {
                Volatile.Write(ref this.storageRoot, root);
            }

            return root;
        }
    }

    /// <summary>
    /// Storage connection string with explicit per-service endpoints — the emulator serves all
    /// three from one port, so the SDK cannot infer them from the account name.
    ///
    /// <para>
    /// The endpoints are built on <see cref="StorageRoot"/> rather than the configured endpoint,
    /// and that is load-bearing. Azure.Storage reads the account out of the URL path
    /// (<c>/devstoreaccount1/container/blob</c>) only when the host is a literal IPv4 address; for
    /// any DNS name it falls back to the production shape, where the account lives in the
    /// subdomain and the first path segment is the *container*. Against
    /// <c>http://localhost:4577/devstoreaccount1</c> the SDK therefore reads "devstoreaccount1" as
    /// the container name, and every blob call lands a segment short — a container create that
    /// returns 201 followed by an upload that 404s with ContainerNotFound. Verified on
    /// Azure.Storage.Blobs 12.29.2, 2026-08-29; see docs/BLAZOR-PLAN.md §14.
    /// </para>
    /// </summary>
    public string StorageConnectionString(string? accountName = null)
    {
        string account = accountName ?? this.AccountName;
        string scheme = this.BaseUri.Scheme;
        string root = this.StorageRoot;

        return $"DefaultEndpointsProtocol={scheme};" +
               $"AccountName={account};" +
               $"AccountKey={this.emulatorOptions.AccountKey};" +
               $"BlobEndpoint={root}/{account};" +
               $"QueueEndpoint={root}/{account};" +
               $"TableEndpoint={root}/{account};";
    }

    /// <summary>
    /// Rewrites the endpoint's host to a literal IPv4 address, which is the only form
    /// Azure.Storage accepts an account-in-path URL in. "localhost" and "::1" both fail the SDK's
    /// test — it wants something <c>IPAddress.TryParse</c> reads as IPv4 — so loopback is mapped
    /// explicitly rather than resolved. A container name on the Compose network ("floci-az") has
    /// to be looked up.
    /// </summary>
    /// <returns>
    /// The rewritten root, and whether it is a settled answer worth caching. An unresolved name
    /// yields <c>false</c>: the endpoint is handed back unchanged so the call fails at the
    /// transport as <c>Unreachable</c>, but the next attempt resolves again.
    /// </returns>
    private static (string Root, bool Resolved) BuildStorageRoot(Uri baseUri)
    {
        (string host, bool resolved) = HostFor(baseUri);

        return ($"{baseUri.Scheme}://{host}:{baseUri.Port}", resolved);

        static (string Host, bool Resolved) HostFor(Uri uri)
        {
            // Literals first, and specifically before the IsLoopback check below: 127.0.0.2 is
            // loopback too, and mapping it to 127.0.0.1 would quietly move the endpoint to a
            // different address than the one configured.
            //
            // Uri.Host returns an IPv6 literal already bracketed ("[::1]") and IPAddress.TryParse
            // accepts that form, so this branch — not the IsLoopback one — is what "::1" hits.
            // Parse DnsSafeHost, which is the unbracketed spelling.
            if (IPAddress.TryParse(uri.DnsSafeHost, out IPAddress? literal))
            {
                if (literal.AddressFamily == AddressFamily.InterNetwork)
                {
                    return (uri.Host, true);
                }

                // An IPv6 literal cannot satisfy the SDK. Loopback has an exact IPv4 spelling for
                // the same machine, so "::1" is safe to rewrite; any other address would be a
                // different host and is a configuration that cannot work.
                if (uri.IsLoopback)
                {
                    return ("127.0.0.1", true);
                }

                throw new InvalidOperationException(UnusableHostMessage(uri.DnsSafeHost));
            }

            if (uri.IsLoopback)
            {
                return ("127.0.0.1", true);
            }

            IPAddress[] addresses;

            try
            {
                addresses = Dns.GetHostAddresses(uri.Host);
            }
            // The name does not resolve, so nothing is listening on it either. Handing back the
            // configured host makes the storage call fail at the transport as Unreachable, which
            // is the honest answer; throwing here would instead surface as a broken sample. Not
            // cached, because a container that has not started yet resolves fine a moment later.
            catch (SocketException)
            {
                return (uri.Host, false);
            }

            IPAddress? v4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);

            if (v4 is not null)
            {
                return (v4.ToString(), true);
            }

            // Resolved, but to IPv6 only. This is the one case that must not fall back to the
            // configured host: the connection would *succeed* and the SDK would still read the
            // account as the container, producing a create-201-then-upload-404 that looks like an
            // emulator bug. Fail loudly and name the constraint instead.
            throw new InvalidOperationException(UnusableHostMessage(uri.Host));
        }

        static string UnusableHostMessage(string host)
            => $"The Azure storage endpoint host '{host}' is not, and does not resolve to, a literal IPv4 address. "
                + "Azure.Storage reads the account name from the URL path only for an IPv4 host, so this endpoint "
                + "would silently address the account as the container. Configure Floci:Azure:Endpoint with an IPv4 "
                + "host (see docs/BLAZOR-PLAN.md §14).";
    }

    private string Combine(string relativePath)
        => $"{this.emulatorOptions.Endpoint.TrimEnd('/')}/{relativePath.TrimStart('/')}";
}
