using FlociLab.Core.Endpoints;
using Google.Cloud.Kms.V1;

namespace FlociLab.Gcp.Kms;

/// <summary>
/// The transport half of plan §7. Cloud KMS is not one of the emulator-aware clients (only
/// Pub/Sub, Firestore and Datastore honour a *_EMULATOR_HOST variable) — it takes the other route
/// named in <see cref="FlociGcpExtensions"/>'s remarks unconditionally:
/// <see cref="FlociGcpExtensions.ForFloci{TClient}"/> against <see cref="GcpEndpoints.GrpcTarget"/>.
/// <see cref="KeyManagementServiceClientBuilder"/> inherits the same
/// <c>ClientBuilderBase&lt;KeyManagementServiceClient&gt;</c> base <c>ForFloci</c> targets, so
/// setting <c>Endpoint</c> and insecure credentials works without touching
/// <c>EmulatorDetection</c> or any environment variable.
///
/// <para>
/// Cached, built on first use, same reasoning as <c>SecretManagerClientFactory</c>: a fresh
/// channel per call pays the loopback connect cost on every operation, and
/// <see cref="KeyManagementServiceClient"/> is thread-safe once built. Not
/// <see cref="IDisposable"/>: the generated client type offers no disposal surface, so the channel
/// lives as long as the process does.
/// </para>
/// </summary>
public sealed class KmsClientFactory(GcpEndpoints endpoints)
{
    private readonly Lock @lock = new();

    private KeyManagementServiceClient? client;

    /// <summary>host:port, for showing the wire-level target alongside the SDK call.</summary>
    public string GrpcTarget => endpoints.GrpcTarget;

    public string ProjectId => endpoints.ProjectId;

    /// <summary>
    /// Cloud KMS key rings live under a region; "global" is the one location every project has
    /// with no regional provisioning of its own, and the emulator accepts it (verified against
    /// floci-gcp 0.7.0, 2026-09-02).
    /// </summary>
    public string LocationId => "global";

    /// <summary>Whether the next client build targets floci-gcp or real Google Cloud.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    public KeyManagementServiceClient Create()
    {
        lock (this.@lock)
        {
            return this.client ??= this.Build();
        }
    }

    private KeyManagementServiceClient Build()
    {
        if (!endpoints.UseEmulator)
        {
            return KeyManagementServiceClient.Create();
        }

        KeyManagementServiceClientBuilder builder = new();
        builder.ForFloci(endpoints);

        return builder.Build();
    }
}
