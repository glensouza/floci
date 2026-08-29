using FlociLab.Core.Endpoints;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Grpc.Core;

namespace FlociLab.Gcp;

/// <summary>
/// The transport half of plan §7, and the provider most likely to fight back. Two mutually
/// exclusive routes to the emulator, plus one that is not gRPC at all:
///
/// <list type="number">
///   <item><b>Emulator-aware clients</b> (Pub/Sub, Firestore, Datastore) —
///         <see cref="UseEmulatorHost"/> to set the *_EMULATOR_HOST variable, then
///         <c>builder.EmulatorDetection = FlociGcpExtensions.Detection</c>. Do not also set an
///         endpoint: emulator detection owns the address and the credentials.</item>
///   <item><b>Every other gRPC client</b> — <see cref="ForFloci"/>, which sets the address, drops
///         to plaintext and pins the adapter.</item>
///   <item><b>REST/JSON clients</b> (Storage) — not gRPC at all: use
///         <c>GcpEndpoints.StorageBaseUri</c>, with the HttpClient fallback in the risk register
///         if the builder ignores it.</item>
/// </list>
/// </summary>
public static class FlociGcpExtensions
{
    public const string PubSubEmulatorHostVariable = "PUBSUB_EMULATOR_HOST";
    public const string FirestoreEmulatorHostVariable = "FIRESTORE_EMULATOR_HOST";
    public const string DatastoreEmulatorHostVariable = "DATASTORE_EMULATOR_HOST";

    /// <summary>
    /// What an emulator-aware builder's <c>EmulatorDetection</c> is set to. EmulatorOnly fails
    /// loudly when the variable is missing, rather than quietly reaching for real Google Cloud
    /// with no credentials.
    /// </summary>
    public const EmulatorDetection Detection = EmulatorDetection.EmulatorOnly;

    /// <summary>
    /// Points a gRPC client builder at the emulator. Everything is multiplexed onto one port over
    /// HTTP/2 with no TLS, so the credentials have to be insecure — a client left on its default
    /// fails the handshake rather than falling back.
    /// </summary>
    public static void ForFloci<TClient>(this ClientBuilderBase<TClient> builder, GcpEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoints);

        builder.Endpoint = endpoints.GrpcTarget;
        builder.ChannelCredentials = ChannelCredentials.Insecure;
        builder.GrpcAdapter = GrpcNetClientAdapter.Default;
    }

    /// <summary>
    /// Sets the host:port variable the emulator-aware clients look for. Only sets it if nothing
    /// else has, so an AppHost- or shell-provided value wins.
    /// </summary>
    public static GcpEndpoints UseEmulatorHost(this GcpEndpoints endpoints, string variableName)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(variableName);

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variableName)))
        {
            Environment.SetEnvironmentVariable(variableName, endpoints.EmulatorHost);
        }

        return endpoints;
    }
}
