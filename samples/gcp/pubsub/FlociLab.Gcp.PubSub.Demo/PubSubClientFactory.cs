using FlociLab.Core.Endpoints;
using Google.Cloud.PubSub.V1;

namespace FlociLab.Gcp.PubSub;

/// <summary>
/// The transport half of plan §7 for the provider it calls hardest. Pub/Sub is one of the
/// emulator-aware clients, but this sample deliberately takes the other route named in
/// <c>FlociGcpExtensions</c>'s remarks: <see cref="FlociGcpExtensions.ForFloci{TClient}"/> against
/// <see cref="GcpEndpoints.GrpcTarget"/>, not <c>UseEmulatorHost</c> plus a process-wide
/// PUBSUB_EMULATOR_HOST variable — a web app binding its endpoint from configuration should not
/// depend on one, same reasoning as <c>StorageClientFactory</c>'s choice of <c>BaseUri</c> over
/// STORAGE_EMULATOR_HOST.
///
/// <para>
/// One client pair for the process, built on first use — the same reasoning and the same lock
/// shape as <c>StorageClientFactory.Create</c>: a fresh gRPC channel per call pays the loopback
/// connect cost on every single operation, and both clients are thread-safe once built. Unlike
/// <c>StorageClient</c>, neither client type here is <c>IDisposable</c> — the channel underneath
/// is owned by GAX's own channel pool, not by this factory.
/// </para>
/// </summary>
public sealed class PubSubClientFactory(GcpEndpoints endpoints)
{
    private readonly Lock @lock = new();

    private PublisherServiceApiClient? publisher;
    private SubscriberServiceApiClient? subscriber;

    /// <summary>host:port, for showing the wire-level target alongside the SDK call.</summary>
    public string GrpcTarget => endpoints.GrpcTarget;

    public string ProjectId => endpoints.ProjectId;

    /// <summary>Whether the next client build targets floci-gcp or real Google Cloud.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    public PublisherServiceApiClient Publisher()
    {
        lock (this.@lock)
        {
            return this.publisher ??= this.BuildPublisher();
        }
    }

    public SubscriberServiceApiClient Subscriber()
    {
        lock (this.@lock)
        {
            return this.subscriber ??= this.BuildSubscriber();
        }
    }

    private PublisherServiceApiClient BuildPublisher()
    {
        if (!endpoints.UseEmulator)
        {
            return PublisherServiceApiClient.Create();
        }

        PublisherServiceApiClientBuilder builder = new();
        builder.ForFloci(endpoints);

        return builder.Build();
    }

    private SubscriberServiceApiClient BuildSubscriber()
    {
        if (!endpoints.UseEmulator)
        {
            return SubscriberServiceApiClient.Create();
        }

        SubscriberServiceApiClientBuilder builder = new();
        builder.ForFloci(endpoints);

        return builder.Build();
    }
}
