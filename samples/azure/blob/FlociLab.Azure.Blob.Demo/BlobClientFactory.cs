using Azure.Storage.Blobs;
using FlociLab.Core.Endpoints;

namespace FlociLab.Azure.Blob;

/// <summary>
/// The whole of the emulator-specific wiring for this sample. Azure is the medium provider
/// (docs/BLAZOR-PLAN.md §7) because it has no single endpoint knob — it has three planes, and
/// storage is the one that takes a connection string. <see cref="AzureEndpoints.StorageConnectionString"/>
/// builds it, including the IPv4-literal host the SDK needs to find the account in the URL path;
/// that constraint is documented at its source and is the only genuinely surprising thing here.
/// </summary>
public sealed class BlobClientFactory(AzureEndpoints endpoints)
{
    /// <summary>Blob endpoint, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => $"{endpoints.StorageRoot}/{endpoints.AccountName}";

    /// <summary>The account the connection string names — <c>devstoreaccount1</c> by default.</summary>
    public string AccountName => endpoints.AccountName;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public BlobServiceClient Create()
    {
        BlobClientOptions options = new();

        // The SDK default is 3 retries with exponential backoff, which against a stopped emulator
        // turns one refused connection into seconds of "Running…". Two reasons to turn it off
        // here: a page whose whole job is to show "the emulator is down" has to say so quickly,
        // and the request shown beside each step is meant to be *the* request — silently sending
        // four would make the page lie about the wire. A production app wants the retries; this is
        // the only emulator-shaped line in the sample that is not the endpoint itself.
        options.Retry.MaxRetries = 0;

        return new BlobServiceClient(endpoints.StorageConnectionString(), options);
    }
}
