using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Security.KeyVault.Secrets;
using FlociLab.Core;

namespace FlociLab.Azure.KeyVaultSecrets;

/// <summary>
/// Azure Key Vault Secrets against floci-az. Ordinary Azure.Security.KeyVault.Secrets code — the
/// only emulator-aware lines are in <see cref="KeyVaultSecretsClientFactory"/> (the TLS-check
/// workaround) and <see cref="FlociAzureExtensions.AllowInsecureBearerToken"/> it calls.
///
/// <para>
/// Getting the client authenticated is not enough to make this sample work: floci-az has two
/// further gaps, both confirmed by curling the running emulator and by its own access log
/// (docs/BLAZOR-PLAN.md §14).
/// </para>
///
/// <list type="bullet">
///   <item>
///     <c>GetPropertiesOfSecretsAsync</c> (list) always fails. The real SDK requests
///     <c>GET secrets/</c> (a trailing slash, confirmed via floci-az's own request log) for a list,
///     but floci-az's router treats the empty segment after the slash as a secret <em>name</em> and
///     answers 404 <c>SecretNotFound</c> instead of listing. <see cref="ProbeAsync"/> therefore
///     reports <see cref="ProbeStatus.Error"/>, not <see cref="ProbeStatus.Ok"/>.
///   </item>
///   <item>
///     Every operation that returns a secret body — <c>SetSecret</c>, <c>GetSecret</c>, the delete
///     response — fails too, for an unrelated reason: floci-az serialises the optional
///     <c>attributes.nbf</c>/<c>attributes.exp</c> fields as JSON <c>null</c> when unset, rather
///     than omitting them. The SDK's model reads them as a Unix-timestamp number and throws
///     <c>System.InvalidOperationException: The requested operation requires an element of type
///     'Number', but the target element has type 'Null'.</c> on the very first response it parses.
///   </item>
/// </list>
///
/// Both are recorded rather than worked around (constraint 6) — this sample is shipped broken
/// against floci-az today, the same choice the Queue Storage sample makes for its own gaps.
/// </summary>
public sealed class KeyVaultSecretsDemo(KeyVaultSecretsClientFactory factory) : IServiceDemo
{
    private const string InitialValue = "Hello from FlociLab.";
    private const string UpdatedValue = "Updated from FlociLab.";

    public string Provider => CloudProvider.Azure;

    public string Slug => "keyvaultsecrets";

    public string DisplayName => "Key Vault Secrets";

    public string Category => "Security";

    public string Route => "/azure/keyvaultsecrets";

    /// <summary>GetPropertiesOfSecrets — one request, no state, and the cheapest call Key Vault has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            SecretClient client = factory.Create();
            int count = 0;

            await foreach (Page<SecretProperties> page in
                client.GetPropertiesOfSecretsAsync(ct).AsPages().ConfigureAwait(false))
            {
                count = page.Values.Count;
                break;
            }

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetPropertiesOfSecrets returned {count} secret(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        SecretClient client = factory.Create();

        // Unique per run, so two runs never collide and a leftover secret from a crashed run never
        // makes the next one fail. Key Vault names are 1-127 chars of alphanumerics and hyphens.
        string name = $"flocilab-kvsecret-{Guid.NewGuid():N}";
        bool created = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListSecrets — before",
                // The trailing slash is the entire point of this step's failure, so the pane has to
                // carry it: the SDK sends "secrets/", floci-az routes the empty segment as a secret
                // name, and "GET /secrets" — the shape without it — is the one that works. Printing
                // the working shape beside the 404 would misattribute the bug to floci-az's list.
                $"GET {factory.ServiceUrl}/secrets/\nclient.GetPropertiesOfSecretsAsync()",
                async () =>
                {
                    int count = 0;

                    await foreach (Page<SecretProperties> page in
                        client.GetPropertiesOfSecretsAsync(ct).AsPages().ConfigureAwait(false))
                    {
                        count += page.Values.Count;
                    }

                    return $"{count} secret(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "SetSecret",
                $"PUT {factory.ServiceUrl}/secrets/{name}\nclient.SetSecretAsync(\"{name}\", \"{InitialValue}\")",
                async () =>
                {
                    // Set before the call, not after: if the PUT lands but the response does not
                    // come back, the secret exists and cleanup has to know about it. Cleanup
                    // treats an absent secret as a no-op, so claiming it early is free.
                    created = true;
                    Response<KeyVaultSecret> response = await client.SetSecretAsync(name, InitialValue, ct).ConfigureAwait(false);

                    return $"HTTP {response.GetRawResponse().Status} — Id: {response.Value.Id}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetSecret",
                $"GET {factory.ServiceUrl}/secrets/{name}\nclient.GetSecretAsync(\"{name}\")",
                async () =>
                {
                    Response<KeyVaultSecret> response = await client.GetSecretAsync(name, cancellationToken: ct).ConfigureAwait(false);

                    // A round-trip that returns something other than what was set did not round
                    // trip — a mismatch goes out red rather than a green badge over a broken read.
                    if (response.Value.Value != InitialValue)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {response.GetRawResponse().Status} — Value was \"{response.Value.Value}\", not the value SetSecret set.");
                    }

                    return $"HTTP {response.GetRawResponse().Status} — Value: {response.Value.Value}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "SetSecret — new version",
                $"PUT {factory.ServiceUrl}/secrets/{name}\nclient.SetSecretAsync(\"{name}\", \"{UpdatedValue}\")",
                async () =>
                {
                    Response<KeyVaultSecret> response = await client.SetSecretAsync(name, UpdatedValue, ct).ConfigureAwait(false);

                    return $"HTTP {response.GetRawResponse().Status} — Version: {response.Value.Properties.Version}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetSecret — after update",
                $"GET {factory.ServiceUrl}/secrets/{name}\nclient.GetSecretAsync(\"{name}\")",
                async () =>
                {
                    Response<KeyVaultSecret> response = await client.GetSecretAsync(name, cancellationToken: ct).ConfigureAwait(false);

                    // Same rule as the first GetSecret: a read that still shows the old value means
                    // SetSecret did not actually create a new current version.
                    if (response.Value.Value != UpdatedValue)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {response.GetRawResponse().Status} — Value was \"{response.Value.Value}\", not the value the second SetSecret set.");
                    }

                    return $"HTTP {response.GetRawResponse().Status} — Value: {response.Value.Value}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean vault. The step it produces is yielded below —
            // an iterator may not yield from inside a finally.
            cleanup = created ? await DeleteSecretAsync(client, name, ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// Azure reports both of the interesting failures inside a <see cref="RequestFailedException"/>,
    /// so <see cref="ProbeResult.FromException"/> — which inspects only the outermost exception —
    /// cannot classify them on its own. A 501 arrives as <see cref="RequestFailedException.Status"/>;
    /// a refused connection to the IMDS token endpoint (walked via the inner-exception chain, since
    /// Azure.Identity wraps it in an <c>AuthenticationFailedException</c>) or to the vault itself
    /// arrives with a transport exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RequestFailedException { Status: (int)HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means something answered, so this is it behaving badly rather than
                // being absent. Stop unwrapping and report the error.
                case RequestFailedException { Status: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Key Vault would not.
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
    /// A soft delete followed by a purge, so a re-run always finds the name free — real Key Vault
    /// keeps a soft-deleted secret's name reserved until the recovery window elapses or it is
    /// purged, the same shape SecretsManagerDemo's <c>ForceDeleteWithoutRecovery</c> avoids for AWS.
    /// The call uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// secret to remove.
    /// </summary>
    private async Task<DemoStep> DeleteSecretAsync(SecretClient client, string name, CancellationToken ct)
    {
        string request = $"DELETE {factory.ServiceUrl}/secrets/{name}\nclient.StartDeleteSecretAsync(\"{name}\")\n"
            + $"DELETE {factory.ServiceUrl}/deletedsecrets/{name}\nclient.PurgeDeletedSecretAsync(\"{name}\")";

        return await RunStepAsync("DeleteSecret — cleanup", request, async () =>
        {
            try
            {
                DeleteSecretOperation operation = await client.StartDeleteSecretAsync(name, CancellationToken.None).ConfigureAwait(false);
                await operation.WaitForCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                await client.PurgeDeletedSecretAsync(name, CancellationToken.None).ConfigureAwait(false);

                return "Deleted and purged"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return "Nothing to remove — the secret was never created.";
            }
        }).ConfigureAwait(false);
    }
}
