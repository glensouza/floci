using FlociLab.Core.Endpoints;
using Microsoft.Azure.Cosmos;

namespace FlociLab.Azure.CosmosDb;

/// <summary>
/// The whole of the emulator-specific wiring for this sample. Cosmos is a data plane
/// (docs/BLAZOR-PLAN.md §7) — a URI in the constructor, built with
/// <see cref="AzureEndpoints.DataPlaneUri"/> rather than the IPv4-literal rewrite the storage plane
/// needs: <c>Microsoft.Azure.Cosmos</c> reads the account from a connection-string field, not from
/// the URL path, so the SDK-vs-DNS-host quirk that drives <c>AzureEndpoints.StorageRoot</c> does not
/// apply here.
///
/// <see cref="CosmosClient"/> is documented as expensive to construct and meant to live for the
/// process lifetime — creating one per operation is a connection pool per operation, which is what
/// billed the GCS comparison column ~2 s per call (plan §14). This factory caches it, so the sample
/// must not <c>using</c> the client anywhere; see docs/RCL-TEMPLATE.md's cached-factory variant.
/// </summary>
public sealed class CosmosDbClientFactory(AzureEndpoints endpoints) : IDisposable
{
    /// <summary>
    /// The Cosmos DB Emulator's single well-known master key, published by Microsoft for use with
    /// any local emulator instance. Not a secret — see
    /// https://learn.microsoft.com/azure/cosmos-db/how-to-develop-emulator. floci-az does not
    /// verify the signature it produces (confirmed by probing the running emulator with a garbage
    /// Authorization header, which still answered 200), but the SDK requires a syntactically valid
    /// key to compute one, so the well-known value is used rather than an arbitrary string.
    /// </summary>
    private const string EmulatorMasterKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private readonly Lock @lock = new();
    private CosmosClient? client;
    private bool disposed;

    /// <summary>
    /// The account endpoint, for showing the wire-level request alongside the SDK call. floci-az
    /// serves the account at a "-cosmos" suffixed path off the same port Blob and Queue share
    /// (confirmed by probing <c>GET /</c> and <c>GET /devstoreaccount1-cosmos/</c>, which answer
    /// identically); the writableLocations in the account response point back at this same path.
    /// </summary>
    public string ServiceUrl => this.AccountEndpoint.ToString().TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci-az or real Azure.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    private Uri AccountEndpoint => endpoints.DataPlaneUri($"{endpoints.AccountName}-cosmos/");

    /// <summary>
    /// A cached client shared across demo runs — see the type-level remarks on why this is not a
    /// fresh client per call. The cache is process-lifetime: this factory and
    /// <see cref="FlociLab.Core.Endpoints.AzureEndpoints"/> are both singletons resolved once, and
    /// <c>AzureEndpoints</c> snapshots its options into a field, so a configuration change does
    /// **not** reach a running process — restart the host to retarget it. Same as the GCP and OCI
    /// samples.
    /// </summary>
    public CosmosClient Create()
    {
        lock (this.@lock)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);

            return this.client ??= this.Build();
        }
    }

    /// <summary>
    /// Under the same lock <see cref="Create"/> takes: without it a <c>Create</c> racing shutdown
    /// could be handed a client disposed a moment later, and any <c>Create</c> after disposal would
    /// silently build a replacement that nothing would ever dispose.
    /// </summary>
    public void Dispose()
    {
        lock (this.@lock)
        {
            this.disposed = true;
            this.client?.Dispose();
            this.client = null;
        }
    }

    private CosmosClient Build()
    {
        CosmosClientOptions options = new()
        {
            // Direct mode dials the TCP addresses the account's partition map hands back, which
            // floci-az has no reason to serve correctly — Gateway mode is plain HTTPS through the
            // account endpoint itself, the only mode that makes sense against an emulator that
            // speaks one HTTP port. Real Cosmos DB Emulator tooling makes the same choice.
            ConnectionMode = ConnectionMode.Gateway,

            // The SDK default retries 429s automatically, which against a stopped emulator turns
            // one refused connection into several seconds of "Running…" before the real error
            // surfaces. Off for the same reason every other factory in this repo turns retries off:
            // the request shown beside each step is meant to be the request that went out.
            MaxRetryAttemptsOnRateLimitedRequests = 0,
        };

        // Real Azure. A full connection string rather than DefaultAzureCredential, same reasoning
        // as BlobClientFactory: reaching for Azure.Identity here would add a second package this
        // sample never otherwise needs, breaking constraint 1 (docs/BLAZOR-PLAN.md §3). Real
        // multi-region topology discovery stays on here — LimitToEndpoint below is emulator-only.
        if (!endpoints.UseEmulator)
        {
            string connectionString = endpoints.RealCloudCosmosConnectionString
                ?? throw new InvalidOperationException(
                    "Floci:Azure:UseEmulator is false but no Floci:Azure:CosmosConnectionString was configured. "
                    + "Supply a real Cosmos DB account connection string through user secrets or an environment "
                    + "variable — never appsettings.json.");

            return new CosmosClient(connectionString, options);
        }

        // LimitToEndpoint is the documented requirement for the Cosmos DB Local Emulator and
        // applies equally to floci-az: without it the SDK tries to discover the account's
        // multi-region topology and keeps retrying that discovery against an endpoint that will
        // never answer it. Measured against a stopped emulator: ProbeAsync took over 20 minutes to
        // report Unreachable without this, instead of failing on the RequestTimeout below.
        options.LimitToEndpoint = true;
        options.RequestTimeout = TimeSpan.FromSeconds(10);

        return new CosmosClient(this.AccountEndpoint.ToString(), EmulatorMasterKey, options);
    }
}
