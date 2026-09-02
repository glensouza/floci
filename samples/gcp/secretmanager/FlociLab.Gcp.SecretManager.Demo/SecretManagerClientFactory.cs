using FlociLab.Core.Endpoints;
using Google.Cloud.SecretManager.V1;

namespace FlociLab.Gcp.SecretManager;

/// <summary>
/// The transport half of plan §7. Secret Manager is not one of the emulator-aware clients (only
/// Pub/Sub, Firestore and Datastore honour a *_EMULATOR_HOST variable) — it takes the other route
/// named in <see cref="FlociGcpExtensions"/>'s remarks unconditionally:
/// <see cref="FlociGcpExtensions.ForFloci{TClient}"/> against <see cref="GcpEndpoints.GrpcTarget"/>.
/// <see cref="SecretManagerServiceClientBuilder"/> inherits the same
/// <c>ClientBuilderBase&lt;SecretManagerServiceClient&gt;</c> base <c>ForFloci</c> targets, so
/// setting <c>Endpoint</c> and insecure credentials works without touching
/// <c>EmulatorDetection</c> or any environment variable.
///
/// <para>
/// Cached, built on first use, same reasoning as <c>PubSubClientFactory</c> and
/// <c>FirestoreClientFactory</c>: a fresh channel per call pays the loopback connect cost on every
/// operation, and <see cref="SecretManagerServiceClient"/> is thread-safe once built. <c>ForFloci</c>
/// always sets <c>ChannelCredentials</c>, which turns off GAX's own channel pool, so this factory
/// owns its channel outright — one per instance, and one only because it is cached. Not
/// <see cref="IDisposable"/>: the generated client type offers no disposal surface, so the channel
/// lives as long as the process does, same as <c>FirestoreDb</c>.
/// </para>
/// </summary>
public sealed class SecretManagerClientFactory(GcpEndpoints endpoints)
{
    private readonly Lock @lock = new();

    private SecretManagerServiceClient? client;

    /// <summary>host:port, for showing the wire-level target alongside the SDK call.</summary>
    public string GrpcTarget => endpoints.GrpcTarget;

    public string ProjectId => endpoints.ProjectId;

    /// <summary>Whether the next client build targets floci-gcp or real Google Cloud.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    public SecretManagerServiceClient Create()
    {
        lock (this.@lock)
        {
            return this.client ??= this.Build();
        }
    }

    private SecretManagerServiceClient Build()
    {
        if (!endpoints.UseEmulator)
        {
            return SecretManagerServiceClient.Create();
        }

        SecretManagerServiceClientBuilder builder = new();
        builder.ForFloci(endpoints);

        return builder.Build();
    }
}
