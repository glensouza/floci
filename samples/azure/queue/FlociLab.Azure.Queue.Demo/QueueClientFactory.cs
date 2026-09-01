using Azure.Storage.Queues;
using FlociLab.Core.Endpoints;

namespace FlociLab.Azure.Queue;

/// <summary>
/// The whole of the emulator-specific wiring for this sample. Queue Storage shares the storage
/// plane with Blob (docs/BLAZOR-PLAN.md §7): one connection string, the same IPv4-literal host
/// rewrite <see cref="AzureEndpoints.StorageConnectionString"/> already applies, and the same
/// account. See the Blob sample's <c>BlobClientFactory</c> for why that rewrite exists.
/// </summary>
public sealed class QueueClientFactory(AzureEndpoints endpoints)
{
    /// <summary>Queue endpoint, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => $"{endpoints.StorageRoot}/{endpoints.AccountName}";

    /// <summary>Whether the next <see cref="Create"/> targets floci-az or real Azure.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public QueueServiceClient Create()
    {
        // Real Azure. Same reasoning as BlobClientFactory: Queue Storage authenticates with an
        // account key rather than a TokenCredential, so real-cloud mode needs a connection string
        // supplied rather than reaching for Azure.Identity and breaking the one-package rule.
        if (!endpoints.UseEmulator)
        {
            string connectionString = endpoints.RealCloudConnectionString
                ?? throw new InvalidOperationException(
                    "Floci:Azure:UseEmulator is false but no Floci:Azure:ConnectionString was configured. "
                    + "Supply a real Azure storage connection string through user secrets or an environment "
                    + "variable — never appsettings.json.");

            return new QueueServiceClient(connectionString);
        }

        QueueClientOptions options = new();

        // Turned off for the same reason as the Blob sample: a page whose whole job is to show
        // "the emulator is down" (or "the emulator does not implement this") has to say so quickly,
        // and the request shown beside each step is meant to be *the* request that went out.
        options.Retry.MaxRetries = 0;

        return new QueueServiceClient(endpoints.StorageConnectionString(), options);
    }
}
