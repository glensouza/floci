using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using FlociLab.Core;

namespace FlociLab.Aws.SecretsManager;

/// <summary>
/// AWS Secrets Manager against floci. Ordinary AWSSDK.SecretsManager code — the only
/// emulator-aware line in the sample is in <see cref="SecretsManagerClientFactory"/>.
/// </summary>
public sealed class SecretsManagerDemo(SecretsManagerClientFactory factory) : IServiceDemo
{
    private const string InitialValue = "Hello from FlociLab.";
    private const string UpdatedValue = "Updated from FlociLab.";

    public string Provider => CloudProvider.Aws;

    public string Slug => "secretsmanager";

    public string DisplayName => "Secrets Manager";

    public string Category => "Security";

    public string Route => "/aws/secretsmanager";

    /// <summary>ListSecrets — one request, no state, and the cheapest call Secrets Manager has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonSecretsManager client = factory.Create();
            ListSecretsResponse response = await client.ListSecretsAsync(new ListSecretsRequest(), ct).ConfigureAwait(false);
            int count = response.SecretList?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListSecrets returned {count} secret(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonSecretsManager client = factory.Create();

        // Unique per run, so two runs never collide and a leftover secret from a crashed run never
        // makes the next one fail. Secrets Manager allows up to 512 chars of
        // alphanumerics/-/_/+/=/./@.
        string name = $"flocilab-secretsmanager-{Guid.NewGuid():N}";
        bool created = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListSecrets — before",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: secretsmanager.ListSecrets\nclient.ListSecretsAsync(new ListSecretsRequest())",
                async () =>
                {
                    ListSecretsResponse response = await client.ListSecretsAsync(new ListSecretsRequest(), ct).ConfigureAwait(false);
                    IEnumerable<string> names = response.SecretList?.Select(s => $"  {s.Name}") ?? [];

                    return $"HTTP {(int)response.HttpStatusCode} — {response.SecretList?.Count ?? 0} secret(s)\n"
                        + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateSecret",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: secretsmanager.CreateSecret\nclient.CreateSecretAsync(new CreateSecretRequest {{ Name = \"{name}\", SecretString = \"{InitialValue}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the secret exists and cleanup has to know about it. Cleanup
                    // treats an absent secret as a no-op, so claiming it early is free.
                    created = true;
                    CreateSecretResponse response = await client.CreateSecretAsync(
                        new CreateSecretRequest { Name = name, SecretString = InitialValue }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — ARN: {response.ARN}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetSecretValue",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: secretsmanager.GetSecretValue\nclient.GetSecretValueAsync(new GetSecretValueRequest {{ SecretId = \"{name}\" }})",
                async () =>
                {
                    GetSecretValueResponse response = await client.GetSecretValueAsync(
                        new GetSecretValueRequest { SecretId = name }, ct).ConfigureAwait(false);

                    // A round-trip that returns something other than what was created did not
                    // round-trip. The lede promises this page shows what floci actually answered,
                    // so a mismatch goes out red rather than a green badge over a broken read.
                    if (response.SecretString != InitialValue)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — SecretString was \"{response.SecretString}\", not the value CreateSecret set.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — SecretString: {response.SecretString}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutSecretValue",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: secretsmanager.PutSecretValue\nclient.PutSecretValueAsync(new PutSecretValueRequest {{ SecretId = \"{name}\", SecretString = \"{UpdatedValue}\" }})",
                async () =>
                {
                    PutSecretValueResponse response = await client.PutSecretValueAsync(
                        new PutSecretValueRequest { SecretId = name, SecretString = UpdatedValue }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — VersionId: {response.VersionId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetSecretValue — after update",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: secretsmanager.GetSecretValue\nclient.GetSecretValueAsync(new GetSecretValueRequest {{ SecretId = \"{name}\" }})",
                async () =>
                {
                    GetSecretValueResponse response = await client.GetSecretValueAsync(
                        new GetSecretValueRequest { SecretId = name }, ct).ConfigureAwait(false);

                    // Same rule as the first GetSecretValue: a read that still shows the old value
                    // means PutSecretValue did not actually create a new current version.
                    if (response.SecretString != UpdatedValue)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — SecretString was \"{response.SecretString}\", not the value PutSecretValue set.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — SecretString: {response.SecretString}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await this.DeleteSecretAsync(client, name, ct).ConfigureAwait(false) : null;
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
    /// the emulator does something real Secrets Manager would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
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
    /// Unlike KMS's <c>ScheduleKeyDeletion</c>, this uses <c>ForceDeleteWithoutRecovery</c> rather
    /// than real Secrets Manager's default 30-day recovery window — the same idempotent-cleanup
    /// shape SQS and DynamoDB use, so a re-run always finds an empty account rather than one more
    /// secret pending deletion. A secret that genuinely never got created answers with
    /// <see cref="ResourceNotFoundException"/>, which is a clean run finishing, not a cleanup
    /// failure worth showing in red. The call uses <see cref="CancellationToken.None"/> — a run
    /// that was cancelled still has a secret to remove.
    /// </summary>
    private async Task<DemoStep> DeleteSecretAsync(IAmazonSecretsManager client, string name, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nX-Amz-Target: secretsmanager.DeleteSecret\nclient.DeleteSecretAsync(new DeleteSecretRequest {{ SecretId = \"{name}\", ForceDeleteWithoutRecovery = true }})";

        return await RunStepAsync("DeleteSecret — cleanup", request, async () =>
        {
            try
            {
                DeleteSecretResponse response = await client.DeleteSecretAsync(
                    new DeleteSecretRequest { SecretId = name, ForceDeleteWithoutRecovery = true }, CancellationToken.None).ConfigureAwait(false);

                // A 200 only says the request was accepted, not that the secret is gone.
                // DeletionDate is the discriminator: with ForceDeleteWithoutRecovery honoured it
                // is ~now, ignored it is the default 30-day recovery window — and a secret in
                // recovery still holds its name, so the next run collides. Verified against floci
                // 1.7.0: forced comes back at now, unforced at exactly now + 2592000 s. A day of
                // tolerance separates those two cases with room to spare and stays immune to any
                // UTC/local confusion in how the epoch is deserialised.
                if (response.DeletionDate is not DateTime deletionDate)
                {
                    throw new InvalidOperationException(
                        $"HTTP {(int)response.HttpStatusCode} — DeleteSecret answered without a DeletionDate, so there is no way to tell whether the secret is gone or merely pending.");
                }

                if (deletionDate.ToUniversalTime() > DateTime.UtcNow.AddDays(1))
                {
                    throw new InvalidOperationException(
                        $"HTTP {(int)response.HttpStatusCode} — ForceDeleteWithoutRecovery was ignored: DeletionDate is {deletionDate.ToUniversalTime():O}, a recovery window rather than an immediate delete. The secret still holds its name, so a re-run would collide.");
                }

                return $"HTTP {(int)response.HttpStatusCode} — removed the secret (DeletionDate {deletionDate.ToUniversalTime():O}, immediate)"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (ResourceNotFoundException)
            {
                return "Nothing to remove — the secret was never created.";
            }
        }).ConfigureAwait(false);
    }
}
