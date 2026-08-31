using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Kms;

/// <summary>
/// AWS Key Management Service against floci. Ordinary AWSSDK.KeyManagementService code — the only
/// emulator-aware line in the sample is in <see cref="KmsClientFactory"/>.
/// </summary>
public sealed class KmsDemo(KmsClientFactory factory) : IServiceDemo
{
    private const string Plaintext = "Hello from FlociLab.";

    public string Provider => CloudProvider.Aws;

    public string Slug => "kms";

    public string DisplayName => "KMS";

    public string Category => "Security";

    public string Route => "/aws/kms";

    /// <summary>ListKeys — one request, no state, and the cheapest call KMS has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonKeyManagementService client = factory.Create();
            ListKeysResponse response = await client.ListKeysAsync(new ListKeysRequest(), ct).ConfigureAwait(false);
            int count = response.Keys?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListKeys returned {count} key(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonKeyManagementService client = factory.Create();

        // Unique per run, so two runs never collide. Unlike a table or queue name, this is not a
        // lookup key — KMS assigns the KeyId server-side and only returns it in the response — but
        // it keeps the key identifiable in a real account while the lab is being recorded.
        string description = $"flocilab-kms-{Guid.NewGuid():N}";
        string? keyId = null;
        byte[] ciphertext = [];

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListKeys — before",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: TrentService.ListKeys\nclient.ListKeysAsync(new ListKeysRequest())",
                async () =>
                {
                    ListKeysResponse response = await client.ListKeysAsync(new ListKeysRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Keys?.Count ?? 0} key(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateKey",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: TrentService.CreateKey\nclient.CreateKeyAsync(new CreateKeyRequest {{ Description = \"{description}\" }})",
                async () =>
                {
                    CreateKeyResponse response = await client.CreateKeyAsync(
                        new CreateKeyRequest { Description = description }, ct).ConfigureAwait(false);

                    // Captured only once the response actually arrives — unlike the table/queue
                    // name in the other Phase 2 samples, KeyId is server-assigned, so there is
                    // nothing to claim before the call, and a lost response leaves nothing to
                    // clean up (see ScheduleKeyDeletionAsync).
                    keyId = KmsResponse.Require(response.KeyMetadata?.KeyId, "CreateKey", "KeyMetadata.KeyId");

                    return $"HTTP {(int)response.HttpStatusCode} — KeyId: {keyId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Encrypt",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: TrentService.Encrypt\nclient.EncryptAsync(new EncryptRequest {{ KeyId = \"{keyId}\", Plaintext = \"{Plaintext}\" }})",
                async () =>
                {
                    byte[] plaintextBytes = Encoding.UTF8.GetBytes(Plaintext);
                    EncryptResponse response = await client.EncryptAsync(
                        new EncryptRequest { KeyId = keyId, Plaintext = new MemoryStream(plaintextBytes) },
                        ct).ConfigureAwait(false);
                    ciphertext = KmsResponse.Require(response.CiphertextBlob, "Encrypt", "CiphertextBlob").ToArray();

                    // The Decrypt step below only checks that the round-trip reproduces what went
                    // in, which an Encrypt that returned the plaintext untouched would satisfy
                    // perfectly — five green steps over a call that encrypted nothing. Same class
                    // of bug as the empty SQS receive and the still-creating DynamoDB table, so
                    // the check belongs here, on the way out.
                    if (ciphertext.Length == 0 || ciphertext.AsSpan().SequenceEqual(plaintextBytes))
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — Encrypt returned {ciphertext.Length} byte(s) that are the plaintext itself; nothing was encrypted.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {ciphertext.Length} byte(s) of ciphertext"
                        + RecoverablePlaintextWarning(ciphertext, plaintextBytes);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Decrypt",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: TrentService.Decrypt\nclient.DecryptAsync(new DecryptRequest {{ CiphertextBlob = <{ciphertext.Length} bytes> }})",
                async () =>
                {
                    DecryptResponse response = await client.DecryptAsync(
                        new DecryptRequest { KeyId = keyId, CiphertextBlob = new MemoryStream(ciphertext) },
                        ct).ConfigureAwait(false);
                    string decrypted = Encoding.UTF8.GetString(
                        KmsResponse.Require(response.Plaintext, "Decrypt", "Plaintext").ToArray());

                    // Same rule as the other samples' round-trip checks: a decrypt that does not
                    // reproduce what was encrypted did not round-trip, so this step does not get a
                    // green badge for it.
                    if (decrypted != Plaintext)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — decrypted \"{decrypted}\" does not match the plaintext that was encrypted.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — plaintext: {decrypted}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // The step it produces is yielded below — an iterator may not yield from inside a
            // finally.
            cleanup = keyId is not null ? await this.ScheduleKeyDeletionAsync(client, keyId, ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// The AWS SDK reports both of the interesting failures inside an
    /// <see cref="AmazonServiceException"/>, so <see cref="ProbeResult.FromException"/> — which
    /// inspects only the outermost exception — cannot classify them on its own. A 501 arrives as
    /// a status code on the exception; a refused connection arrives with no status code at all
    /// and a transport exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real KMS would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still schedules the key's
        // deletion. Catching it here would instead fabricate a "Failed" step for every remaining
        // operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// floci 1.7.0 does not encrypt. Its CiphertextBlob is the ASCII envelope
    /// <c>kms:v2:&lt;KeyId&gt;:&lt;16 hex&gt;::&lt;base64 plaintext&gt;</c>, so the plaintext comes
    /// back out with two base64 decodes and no key at all (plan §14, verified 2026-08-31). The API
    /// contract around it is modelled properly — decrypting under the wrong key still raises
    /// <c>IncorrectKeyException</c> — so the round-trip is a real test of the SDK wiring, but the
    /// confidentiality is theatre. The page says so out loud rather than showing a green badge over
    /// it, because a viewer who discovers this for themselves stops trusting the rest of the demo.
    /// </summary>
    private static string RecoverablePlaintextWarning(byte[] ciphertext, byte[] plaintext)
    {
        if (!Encoding.UTF8.GetString(ciphertext).Contains(Convert.ToBase64String(plaintext), StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return "\nNOTE: the plaintext is recoverable from this blob — floci stores it base64-encoded"
            + " inside a \"kms:v2:…\" envelope rather than encrypting it. Never treat emulator"
            + " ciphertext as protected, and never let it reach anything real.";
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// KMS has no immediate delete. <c>ScheduleKeyDeletion</c> with the API's minimum seven-day
    /// window is what production code calls "deleting a key" — real AWS keeps it listed as
    /// <c>PendingDeletion</c> for the window so an accidental delete can still be cancelled. Unlike
    /// the DynamoDB and SQS samples, a re-run of this page does not return the account to its
    /// pre-run key count; it schedules one more key for deletion. The call uses
    /// <see cref="CancellationToken.None"/> — a run that was cancelled still has a key to schedule.
    /// </summary>
    private async Task<DemoStep> ScheduleKeyDeletionAsync(IAmazonKeyManagementService client, string keyId, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nX-Amz-Target: TrentService.ScheduleKeyDeletion\nclient.ScheduleKeyDeletionAsync(new ScheduleKeyDeletionRequest {{ KeyId = \"{keyId}\", PendingWindowInDays = 7 }})";

        return await RunStepAsync("ScheduleKeyDeletion — cleanup", request, async () =>
        {
            ScheduleKeyDeletionResponse response = await client.ScheduleKeyDeletionAsync(
                new ScheduleKeyDeletionRequest { KeyId = keyId, PendingWindowInDays = 7 }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — deletion scheduled for {response.DeletionDate:u}"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
