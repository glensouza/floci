using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using FlociLab.Core;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Grpc.Core;

namespace FlociLab.Gcp.SecretManager;

/// <summary>
/// Google Cloud Secret Manager against floci-gcp. Ordinary Google.Cloud.SecretManager.V1 code —
/// the only emulator-aware lines in the sample are in <see cref="SecretManagerClientFactory"/>.
///
/// <para>
/// Unlike AWS Secrets Manager or Key Vault, Secret Manager separates the secret container from its
/// value: <c>CreateSecret</c> names the container and its replication policy but carries no
/// payload, and a value only exists once <c>AddSecretVersion</c> creates one. So the round-trip
/// below has an explicit AddSecretVersion step the AWS and Azure samples do not need, and reading
/// the value back always goes through the <c>"latest"</c> version alias rather than the secret
/// name itself.
/// </para>
/// </summary>
public sealed class SecretManagerDemo(SecretManagerClientFactory factory) : IServiceDemo
{
    private const string InitialValue = "Hello from FlociLab.";
    private const string UpdatedValue = "Updated from FlociLab.";

    public string Provider => CloudProvider.Gcp;

    public string Slug => "secretmanager";

    public string DisplayName => "Secret Manager";

    public string Category => "Security";

    public string Route => "/gcp/secretmanager";

    /// <summary>ListSecrets — one request, no state, and the cheapest call Secret Manager has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            SecretManagerServiceClient client = factory.Create();
            int count = 0;

            await foreach (Secret secret in client.ListSecretsAsync(ProjectName.FromProject(factory.ProjectId))
                .WithCancellation(ct).ConfigureAwait(false))
            {
                _ = secret;
                count++;
            }

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListSecrets returned {count} secret(s).");
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
        SecretManagerServiceClient client = factory.Create();

        // Unique per run, so two runs never collide and a leftover secret from a crashed run never
        // makes the next one fail. Secret Manager allows up to 255 chars of letters/digits/-/_.
        string secretId = $"flocilab-secretmanager-{Guid.NewGuid():N}";
        SecretName secretName = new(factory.ProjectId, secretId);
        SecretVersionName latestVersionName = new(factory.ProjectId, secretId, "latest");
        bool created = false;
        bool createConfirmed = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListSecrets — before",
                $"{factory.GrpcTarget} google.cloud.secretmanager.v1.SecretManagerService/ListSecrets\nclient.ListSecretsAsync(\"projects/{factory.ProjectId}\")",
                ct,
                async () =>
                {
                    List<string> names = [];

                    await foreach (Secret secret in client.ListSecretsAsync(ProjectName.FromProject(factory.ProjectId))
                        .WithCancellation(ct).ConfigureAwait(false))
                    {
                        names.Add($"  {secret.Name}");
                    }

                    return $"{names.Count} secret(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateSecret",
                $"{factory.GrpcTarget} google.cloud.secretmanager.v1.SecretManagerService/CreateSecret\n"
                    + $"client.CreateSecretAsync(\"projects/{factory.ProjectId}\", \"{secretId}\", replication: Automatic)",
                ct,
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the secret container exists and cleanup has to know about it.
                    // Cleanup treats an absent secret as a no-op, so claiming it early is free.
                    created = true;
                    Secret response = await client.CreateSecretAsync(
                        ProjectName.FromProject(factory.ProjectId),
                        secretId,
                        new Secret { Replication = new Replication { Automatic = new Replication.Types.Automatic() } },
                        ct).ConfigureAwait(false);

                    // Distinct from created: that one says "a CreateSecret went out, so cleanup has
                    // to try", this one says "the container demonstrably exists". Cleanup needs both
                    // — see DeleteSecretAsync for why a delete that removed nothing is not a success.
                    createConfirmed = true;

                    return $"Secret {response.Name}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AddSecretVersion",
                $"{factory.GrpcTarget} google.cloud.secretmanager.v1.SecretManagerService/AddSecretVersion\n"
                    + $"client.AddSecretVersionAsync(\"{secretName}\", payload: \"{InitialValue}\")",
                ct,
                async () =>
                {
                    SecretVersion response = await client.AddSecretVersionAsync(
                        secretName, new SecretPayload { Data = ByteString.CopyFromUtf8(InitialValue) }, ct)
                        .ConfigureAwait(false);

                    return $"SecretVersion {response.Name}\n  state: {response.State}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AccessSecretVersion",
                $"{factory.GrpcTarget} google.cloud.secretmanager.v1.SecretManagerService/AccessSecretVersion\nclient.AccessSecretVersionAsync(\"{latestVersionName}\")",
                ct,
                async () =>
                {
                    AccessSecretVersionResponse response = await client.AccessSecretVersionAsync(latestVersionName, ct)
                        .ConfigureAwait(false);
                    string value = SecretManagerResponse.Require(response.Payload, "AccessSecretVersion", "a payload")
                        .Data.ToStringUtf8();

                    // A round-trip that returns something other than what was created did not
                    // round-trip. The lede promises this page shows what floci-gcp actually
                    // answered, so a mismatch goes out red rather than a green badge over a broken
                    // read.
                    if (value != InitialValue)
                    {
                        throw new InvalidOperationException($"Payload was \"{value}\", not the value AddSecretVersion set.");
                    }

                    return $"Payload: {value}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AddSecretVersion — update",
                $"{factory.GrpcTarget} google.cloud.secretmanager.v1.SecretManagerService/AddSecretVersion\n"
                    + $"client.AddSecretVersionAsync(\"{secretName}\", payload: \"{UpdatedValue}\")",
                ct,
                async () =>
                {
                    SecretVersion response = await client.AddSecretVersionAsync(
                        secretName, new SecretPayload { Data = ByteString.CopyFromUtf8(UpdatedValue) }, ct)
                        .ConfigureAwait(false);

                    return $"SecretVersion {response.Name}\n  state: {response.State}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AccessSecretVersion — after update",
                $"{factory.GrpcTarget} google.cloud.secretmanager.v1.SecretManagerService/AccessSecretVersion\nclient.AccessSecretVersionAsync(\"{latestVersionName}\")",
                ct,
                async () =>
                {
                    AccessSecretVersionResponse response = await client.AccessSecretVersionAsync(latestVersionName, ct)
                        .ConfigureAwait(false);
                    string value = SecretManagerResponse.Require(response.Payload, "AccessSecretVersion", "a payload")
                        .Data.ToStringUtf8();

                    // Same rule as the first AccessSecretVersion: reading "latest" and still
                    // getting the old value means the second AddSecretVersion did not actually
                    // become the new latest.
                    if (value != UpdatedValue)
                    {
                        throw new InvalidOperationException($"Payload was \"{value}\", not the value the second AddSecretVersion set.");
                    }

                    return $"Payload: {value}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean project. Yielded below — an iterator may not
            // yield from inside a finally.
            cleanup = created
                ? await DeleteSecretAsync(client, secretName, createConfirmed).ConfigureAwait(false)
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
    /// carried it. Same reasoning as <c>PubSubDemo.IsCancellation</c> / <c>FirestoreDemo.IsCancellation</c>.
    /// </summary>
    private static bool IsCancellation(RpcException ex, CancellationToken ct)
        => ct.IsCancellationRequested && ex.StatusCode == StatusCode.Cancelled;

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Secret Manager would not.
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
        // stops the run at the step it reached, and RunAsync's finally still removes the secret.
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
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
    /// Cleanup, and a step like any other: it goes green only when it actually removed the secret.
    /// Uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a secret to
    /// remove. Unlike AWS Secrets Manager, real Secret Manager's <c>DeleteSecret</c> has no
    /// recovery window to defeat — it deletes the container and every version immediately, so
    /// there is no ForceDeleteWithoutRecovery-style flag to get right.
    ///
    /// <para>
    /// <c>NotFound</c> is a failure, not a quiet success. <c>created</c> is claimed before
    /// <c>CreateSecret</c> is called (the request may land without a response), so a
    /// <c>CreateSecret</c> that failed outright still reaches this step — and reporting "nothing to
    /// remove" in green would end a run of red steps on a green badge, which is the cleanup case
    /// docs/BLAZOR-PLAN.md §14 records. Unlike Firestore's idempotent delete there is no
    /// precondition to push into the request: floci-gcp 0.7.0 answers a delete of a secret that was
    /// never created with <c>NOT_FOUND</c> ("Secret not found: projects/.../secrets/..."), so the
    /// status alone is the postcondition, and <c>createConfirmed</c> is what tells the two causes
    /// apart.
    /// </para>
    /// </summary>
    private static async Task<DemoStep> DeleteSecretAsync(SecretManagerServiceClient client, SecretName secretName, bool createConfirmed)
    {
        string request = $"google.cloud.secretmanager.v1.SecretManagerService/DeleteSecret\nclient.DeleteSecretAsync(\"{secretName}\")";

        return await RunStepAsync("DeleteSecret — cleanup", request, CancellationToken.None, async () =>
        {
            try
            {
                await client.DeleteSecretAsync(secretName).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    "Nothing was removed: " + (createConfirmed
                        ? $"'{secretName.SecretId}' was created by this run but is already gone, so something else deleted it."
                        : $"'{secretName.SecretId}' never existed, because CreateSecret above did not succeed."),
                    ex);
            }

            return "Removed the secret.";
        }).ConfigureAwait(false);
    }
}
