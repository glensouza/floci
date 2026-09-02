using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using FlociLab.Core;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Grpc.Core;

namespace FlociLab.Gcp.Kms;

/// <summary>
/// Google Cloud KMS against floci-gcp. Ordinary Google.Cloud.Kms.V1 code — the only
/// emulator-aware lines in the sample are in <see cref="KmsClientFactory"/>.
///
/// <para>
/// Cloud KMS has one more level of hierarchy than AWS KMS or Key Vault: a key ring holds crypto
/// keys, and neither a key ring nor a crypto key can ever be deleted — only individual crypto key
/// versions can be scheduled for destruction. Real production code provisions a key ring once,
/// the way it would an S3 bucket, and creates crypto keys inside it per use case; this sample does
/// the same, reusing the fixed key ring <see cref="KeyRingId"/> across every run rather than
/// leaving a fresh, permanently undeletable key ring behind on each one. The crypto key created
/// per run is still permanent — see the "DestroyCryptoKeyVersion — cleanup" step — which is real
/// Cloud KMS behaviour, not a floci quirk.
/// </para>
/// </summary>
public sealed class KmsDemo(KmsClientFactory factory) : IServiceDemo
{
    private const string KeyRingId = "flocilab";
    private const string Plaintext = "Hello from FlociLab.";

    public string Provider => CloudProvider.Gcp;

    public string Slug => "kms";

    public string DisplayName => "Cloud KMS";

    public string Category => "Security";

    public string Route => "/gcp/kms";

    /// <summary>ListKeyRings — one request, no state, and the cheapest call Cloud KMS has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            KeyManagementServiceClient client = factory.Create();
            int count = 0;

            await foreach (KeyRing keyRing in client.ListKeyRingsAsync(this.LocationName()).WithCancellation(ct).ConfigureAwait(false))
            {
                _ = keyRing;
                count++;
            }

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListKeyRings returned {count} key ring(s).");
        }
        catch (RpcException ex) when (IsCancellation(ex, ct))
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        KeyManagementServiceClient client = factory.Create();
        LocationName location = this.LocationName();
        KeyRingName keyRingName = new(factory.ProjectId, factory.LocationId, KeyRingId);

        // Unique per run, so two runs never collide. Unlike the key ring, this crypto key is never
        // reused — see the class remarks on why that still leaves permanent state behind.
        string cryptoKeyId = $"flocilab-kms-{Guid.NewGuid():N}";
        CryptoKeyName cryptoKeyName = new(factory.ProjectId, factory.LocationId, KeyRingId, cryptoKeyId);
        byte[] ciphertext = [];

        // Split into "attempted" and "confirmed", the shape plan §14 settled on: a CreateCryptoKey
        // whose request lands but whose response is lost has still created the crypto key *and* its
        // enabled version 1, and a Cloud KMS crypto key can never be deleted — so treating a lost
        // response as "nothing to clean up" leaves live key material behind permanently. Confirmed
        // wins when it is there; otherwise the cleanup step goes and asks the server.
        bool createAttempted = false;
        string? primaryVersionName = null;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListKeyRings — before",
                $"{factory.GrpcTarget} google.cloud.kms.v1.KeyManagementService/ListKeyRings\nclient.ListKeyRingsAsync(\"{location}\")",
                ct,
                async () =>
                {
                    List<string> names = [];

                    await foreach (KeyRing keyRing in client.ListKeyRingsAsync(location).WithCancellation(ct).ConfigureAwait(false))
                    {
                        names.Add($"  {keyRing.Name}");
                    }

                    return $"{names.Count} key ring(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateKeyRing",
                $"{factory.GrpcTarget} google.cloud.kms.v1.KeyManagementService/CreateKeyRing\nclient.CreateKeyRingAsync(\"{location}\", \"{KeyRingId}\")",
                ct,
                async () =>
                {
                    try
                    {
                        KeyRing response = await client.CreateKeyRingAsync(location, KeyRingId, new KeyRing(), ct).ConfigureAwait(false);

                        return $"KeyRing {response.Name}";
                    }
                    // Key rings can never be deleted, so every run after the first finds this one
                    // already there. Reusing it is the point (see the class remarks) — a real
                    // AlreadyExists is what a correctly-idempotent provisioning step looks like,
                    // not a failure to report.
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
                    {
                        return $"KeyRing {keyRingName} already exists — reusing it.";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateCryptoKey",
                $"{factory.GrpcTarget} google.cloud.kms.v1.KeyManagementService/CreateCryptoKey\n"
                    + $"client.CreateCryptoKeyAsync(\"{keyRingName}\", \"{cryptoKeyId}\", purpose: EncryptDecrypt)",
                ct,
                async () =>
                {
                    createAttempted = true;

                    CryptoKey response = await client.CreateCryptoKeyAsync(
                        keyRingName,
                        cryptoKeyId,
                        new CryptoKey { Purpose = CryptoKey.Types.CryptoKeyPurpose.EncryptDecrypt },
                        ct).ConfigureAwait(false);

                    primaryVersionName = KmsResponse.Require(response.Primary?.Name, "CreateCryptoKey", "Primary.Name");
                    CryptoKeyVersion.Types.CryptoKeyVersionState state = response.Primary!.State;

                    // The state is this step's postcondition, not decoration (plan §14). floci-gcp
                    // generates version 1 synchronously and answers ENABLED, but real Cloud KMS may
                    // answer PENDING_GENERATION — and this page runs against real Cloud KMS whenever
                    // UseEmulator is false. A green badge over a pending version would mean an
                    // Encrypt failing FAILED_PRECONDITION two steps later with nothing naming the
                    // cause, and a cleanup that cannot destroy a version still being generated.
                    if (state != CryptoKeyVersion.Types.CryptoKeyVersionState.Enabled)
                    {
                        throw new InvalidOperationException(
                            $"CreateCryptoKey answered, but its primary version is {state}, not ENABLED — it cannot encrypt yet.");
                    }

                    return $"CryptoKey {response.Name}\n  primary: {primaryVersionName} ({state})";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Encrypt",
                $"{factory.GrpcTarget} google.cloud.kms.v1.KeyManagementService/Encrypt\nclient.EncryptAsync(\"{cryptoKeyName}\", \"{Plaintext}\")",
                ct,
                async () =>
                {
                    byte[] plaintextBytes = Encoding.UTF8.GetBytes(Plaintext);
                    EncryptResponse response = await client.EncryptAsync(
                        cryptoKeyName, ByteString.CopyFromUtf8(Plaintext), ct).ConfigureAwait(false);
                    ciphertext = response.Ciphertext.ToByteArray();

                    // Unlike floci's AWS KMS emulator (plan §14), floci-gcp performs real symmetric
                    // encryption — verified by curl against 0.7.0, 2026-09-02: 32 bytes of binary
                    // ciphertext with the plaintext nowhere inside it. Checked here anyway, on the
                    // way out, for the same reason the AWS sample checks it: a Decrypt round-trip
                    // alone cannot see an Encrypt that quietly did nothing, because a no-op encrypt
                    // round-trips perfectly.
                    if (ciphertext.Length == 0)
                    {
                        throw new InvalidOperationException("Encrypt answered, but with no ciphertext at all.");
                    }

                    if (ciphertext.AsSpan().SequenceEqual(plaintextBytes))
                    {
                        throw new InvalidOperationException(
                            $"Encrypt returned {ciphertext.Length} byte(s) that are the plaintext itself; nothing was encrypted.");
                    }

                    // The subtler no-op, and the one floci's AWS KMS actually ships: an envelope
                    // that merely wraps the base64 plaintext (kms:v2:<KeyId>:<hex>::<base64>) passes
                    // the byte-equality check above while staying recoverable with no key at all.
                    // The AWS sample only warns, because there it is a known live limitation; here
                    // it fails the step, because floci-gcp does encrypt today, so anything else is
                    // a regression rather than a documented gap.
                    if (Encoding.UTF8.GetString(ciphertext).Contains(Convert.ToBase64String(plaintextBytes), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Encrypt returned a blob the plaintext is recoverable from — it was wrapped, not encrypted.");
                    }

                    return $"{ciphertext.Length} byte(s) of ciphertext";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Decrypt",
                $"{factory.GrpcTarget} google.cloud.kms.v1.KeyManagementService/Decrypt\nclient.DecryptAsync(\"{cryptoKeyName}\", <{ciphertext.Length} bytes>)",
                ct,
                async () =>
                {
                    DecryptResponse response = await client.DecryptAsync(
                        cryptoKeyName, ByteString.CopyFrom(ciphertext), ct).ConfigureAwait(false);
                    string decrypted = response.Plaintext.ToStringUtf8();

                    // Same rule as every other sample's round-trip check: a decrypt that does not
                    // reproduce what was encrypted did not round-trip.
                    if (decrypted != Plaintext)
                    {
                        throw new InvalidOperationException($"Decrypted \"{decrypted}\" does not match the plaintext that was encrypted.");
                    }

                    return $"Plaintext: {decrypted}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // Yielded below — an iterator may not yield from inside a finally.
            cleanup = primaryVersionName is not null
                ? await DestroyPrimaryVersionAsync(client, primaryVersionName).ConfigureAwait(false)
                : createAttempted
                    ? await DestroyUnconfirmedKeyAsync(client, cryptoKeyName).ConfigureAwait(false)
                    : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> cannot see a gRPC status hiding inside an
    /// <see cref="RpcException"/>, which is where this SDK puts every answer the server gave. A
    /// refused connection surfaces as <see cref="StatusCode.Unavailable"/> too, so the transport
    /// case has to be told apart from the emulator genuinely answering "unavailable" — which
    /// floci-gcp does not do, so treating every Unavailable as unreachable is the honest read here.
    /// Same classifier shape as <c>SecretManagerDemo</c>, <c>PubSubDemo</c> and <c>FirestoreDemo</c>.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RpcException { StatusCode: StatusCode.Unimplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                // DeadlineExceeded is GAX's own per-call expiration rather than this token: the
                // emulator accepted the connection and never answered, which is the same story
                // Unavailable tells and must not read as the sample being broken.
                case RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded }:
                case SocketException or TimeoutException:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // Any other status means the emulator answered, so this is it behaving badly
                // rather than being absent. Stop unwrapping and report the error.
                case RpcException:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Whether an <see cref="RpcException"/> is this token being cancelled rather than the server
    /// answering. Only a token already cancelled when the call starts throws
    /// <see cref="OperationCanceledException"/>; one that trips mid-flight surfaces as
    /// <see cref="StatusCode.Cancelled"/> instead, because the SDK reports it the way the wire
    /// carried it. Same reasoning as <c>SecretManagerDemo.IsCancellation</c>.
    /// </summary>
    private static bool IsCancellation(RpcException ex, CancellationToken ct)
        => ct.IsCancellationRequested && ex.StatusCode == StatusCode.Cancelled;

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Cloud KMS would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, CancellationToken ct, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        catch (RpcException ex) when (IsCancellation(ex, ct))
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still destroys the crypto
        // key version. Catching it here would instead fabricate a "Failed" step for every
        // remaining operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// The closest Cloud KMS gets to "delete" (see the class remarks) — the crypto key and its key
    /// ring stay forever, but this schedules the primary version's key material for destruction.
    /// Uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a version to
    /// schedule. Unlike Secret Manager's delete, this is idempotent — probed against floci-gcp
    /// 0.7.0, 2026-09-02: destroying an already-scheduled version answers 200 rather than
    /// NOT_FOUND — so the postcondition here is the returned state, not the call succeeding at all.
    /// </summary>
    private static async Task<DemoStep> DestroyPrimaryVersionAsync(KeyManagementServiceClient client, string primaryVersionName)
    {
        string request = $"google.cloud.kms.v1.KeyManagementService/DestroyCryptoKeyVersion\nclient.DestroyCryptoKeyVersionAsync(\"{primaryVersionName}\")";

        return await RunStepAsync("DestroyCryptoKeyVersion — cleanup", request, CancellationToken.None, async () =>
        {
            CryptoKeyVersion response = await client.DestroyCryptoKeyVersionAsync(
                CryptoKeyVersionName.Parse(primaryVersionName), CancellationToken.None).ConfigureAwait(false);

            if (response.State != CryptoKeyVersion.Types.CryptoKeyVersionState.DestroyScheduled)
            {
                throw new InvalidOperationException($"DestroyCryptoKeyVersion answered but left the version in state {response.State}, not DESTROY_SCHEDULED.");
            }

            return $"Scheduled for destruction at {response.DestroyTime?.ToDateTimeOffset():u}";
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleanup for the case <see cref="DestroyPrimaryVersionAsync"/> cannot cover: CreateCryptoKey
    /// was issued but no response came back, so there is no confirmed version name and yet the key
    /// may well exist. Rather than guess the version id, this asks the server — a GetCryptoKey
    /// answering NOT_FOUND is proof nothing was created, which is a truthful green step, and one
    /// that answers with the key hands back the primary version to destroy. Uses
    /// <see cref="CancellationToken.None"/>: a cancelled run is exactly how this path is reached.
    /// </summary>
    private static async Task<DemoStep> DestroyUnconfirmedKeyAsync(KeyManagementServiceClient client, CryptoKeyName cryptoKeyName)
    {
        string request = $"google.cloud.kms.v1.KeyManagementService/GetCryptoKey\nclient.GetCryptoKeyAsync(\"{cryptoKeyName}\")";

        return await RunStepAsync("DestroyCryptoKeyVersion — cleanup", request, CancellationToken.None, async () =>
        {
            CryptoKey cryptoKey;

            try
            {
                cryptoKey = await client.GetCryptoKeyAsync(cryptoKeyName, CancellationToken.None).ConfigureAwait(false);
            }
            // Not a swallow: NOT_FOUND is the answer this step came to get — the create never
            // landed, so there is genuinely nothing to destroy, and saying so is the honest result.
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return "CreateCryptoKey never landed — there is no key to destroy.";
            }

            string versionName = KmsResponse.Require(cryptoKey.Primary?.Name, "GetCryptoKey", "Primary.Name");
            CryptoKeyVersion response = await client.DestroyCryptoKeyVersionAsync(
                CryptoKeyVersionName.Parse(versionName), CancellationToken.None).ConfigureAwait(false);

            if (response.State != CryptoKeyVersion.Types.CryptoKeyVersionState.DestroyScheduled)
            {
                throw new InvalidOperationException($"DestroyCryptoKeyVersion answered but left the version in state {response.State}, not DESTROY_SCHEDULED.");
            }

            return $"CreateCryptoKey landed without answering; destroyed {versionName}, scheduled at {response.DestroyTime?.ToDateTimeOffset():u}";
        }).ConfigureAwait(false);
    }

    private LocationName LocationName() => new(factory.ProjectId, factory.LocationId);
}
