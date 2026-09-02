using FlociLab.Core.Endpoints;
using Google.Cloud.Firestore;

namespace FlociLab.Gcp.Firestore;

/// <summary>
/// The transport half of plan §7. <see cref="FirestoreDbBuilder"/> is one of the emulator-aware
/// builders (it honours FIRESTORE_EMULATOR_HOST via <c>EmulatorDetection</c>), but this factory
/// deliberately takes the other route named in <see cref="FlociGcpExtensions"/>'s remarks — the
/// same one <c>PubSubClientFactory</c> takes: <see cref="FlociGcpExtensions.ForFloci{TClient}"/>
/// against <see cref="GcpEndpoints.GrpcTarget"/> directly. <c>FirestoreDbBuilder</c> inherits
/// <c>ClientBuilderBase&lt;FirestoreDb&gt;</c>, the same base <c>ForFloci</c> targets, so setting
/// <c>Endpoint</c> and insecure credentials works without ever touching
/// <c>EmulatorDetection</c> or the environment variable.
///
/// <para>
/// That matters beyond style here: FIRESTORE_EMULATOR_HOST is process-wide, and
/// <see cref="FlociGcpExtensions.UseEmulatorHost"/> only ever sets it once — a second
/// <see cref="GcpEndpoints"/> pointed at a different port (a second test class's throwaway
/// container, for instance) would silently reuse the first one's address. <c>ForFloci</c> reads
/// the endpoint from <paramref name="endpoints"/> on every build instead, so each factory instance
/// really does target the emulator it was constructed with.
/// </para>
///
/// <para>
/// Cached, built on first use, same reasoning as <c>PubSubClientFactory</c>: a fresh channel per
/// call pays the loopback connect cost on every operation, and <see cref="FirestoreDb"/> is
/// thread-safe once built. Caching matters more here than it looks, because there is no channel
/// pool underneath to fall back on: GAX pools only while the builder leaves credentials alone, and
/// <c>ForFloci</c> always sets <c>ChannelCredentials</c>, which turns <c>CanUseChannelPool</c>
/// off. So this factory owns its channel outright — one per instance, and one only because it is
/// cached. Still not <see cref="IDisposable"/>, but for the plainer reason that there is nothing to
/// dispose: <see cref="FirestoreDb"/> offers no <c>IDisposable</c>, no <c>IAsyncDisposable</c> and
/// no shutdown method, so the channel lives as long as the process does. Right for a singleton a
/// host holds for its lifetime; worth knowing before copying this shape somewhere that builds a
/// factory per request.
/// </para>
/// </summary>
public sealed class FirestoreClientFactory(GcpEndpoints endpoints)
{
    private readonly Lock @lock = new();

    private FirestoreDb? db;

    /// <summary>host:port, for showing the wire-level target alongside the SDK call.</summary>
    public string GrpcTarget => endpoints.GrpcTarget;

    public string ProjectId => endpoints.ProjectId;

    /// <summary>Whether the next client build targets floci-gcp or real Google Cloud.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    public FirestoreDb Create()
    {
        lock (this.@lock)
        {
            return this.db ??= this.Build();
        }
    }

    private FirestoreDb Build()
    {
        if (!endpoints.UseEmulator)
        {
            return FirestoreDb.Create(endpoints.ProjectId);
        }

        FirestoreDbBuilder builder = new() { ProjectId = endpoints.ProjectId };
        builder.ForFloci(endpoints);

        return builder.Build();
    }
}
