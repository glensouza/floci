using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using FlociLab.Core;
using Oci.Common.Model;
using Oci.SecretsService;
using Oci.SecretsService.Models;
using Oci.SecretsService.Requests;
using Oci.SecretsService.Responses;
using Oci.VaultService;
using Oci.VaultService.Models;
using Oci.VaultService.Requests;
using Oci.VaultService.Responses;

namespace FlociLab.Oci.Secrets;

/// <summary>
/// OCI Vault Secrets against floci-oci. Ordinary code against two official SDK packages — the only
/// emulator-aware lines in the sample are in <see cref="SecretsClientFactory"/>.
///
/// <para>
/// Real OCI splits this service across two planes that ship separately: secret CRUD through
/// <see cref="VaultsClient"/> (<c>OCI.DotNetSDK.Vault</c>) and reading the decrypted value through
/// <see cref="SecretsClient"/> (<c>OCI.DotNetSDK.Secrets</c>). That is the provider's own
/// control-plane/data-plane split, not a workaround.
/// </para>
///
/// <para>
/// <c>CreateSecret</c> hard-requires a <c>vaultId</c> and a <c>keyId</c> (floci-oci 400s
/// <c>MissingParameter</c> on either being absent, confirmed by curl 2026-09-02). This sample takes
/// both from configuration rather than provisioning them, which keeps
/// <c>OCI.DotNetSDK.Keymanagement</c> out of its <c>.csproj</c> and matches how production reaches
/// a vault anyway. The OCI Vault page is the one that creates them.
/// </para>
///
/// <para>
/// Unlike the vault and key it depends on, <c>CreateSecret</c> answers <c>ACTIVE</c> directly on
/// floci-oci with no <c>CREATING</c> state to wait out, so this sample needs no
/// <c>Waiters</c>-style poll for the secret itself.
/// </para>
/// </summary>
public sealed class SecretsDemo(SecretsClientFactory factory) : IServiceDemo
{
    private const string InitialValue = "Hello from FlociLab.";
    private const string UpdatedValue = "Updated from FlociLab.";

    public string Provider => CloudProvider.Oci;

    public string Slug => "secrets";

    public string DisplayName => "Secrets";

    public string Category => "Security";

    public string Route => "/oci/secrets";

    /// <summary>ListSecrets — one request, no state, and the cheapest call the service answers.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            VaultsClient secretsManagement = factory.CreateSecretsManagement();
            ListSecretsResponse response = await secretsManagement.ListSecrets(
                new ListSecretsRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListSecrets returned {response.Items.Count} secret(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Building a client can itself fail: the real-cloud branch of the factory refuses a run
        // that would write secrets into the lab's synthetic compartment. That has to become a
        // failed step like any other — an iterator that throws on the first MoveNextAsync takes
        // down the circuit instead of rendering the reason. Caught here and yielded below, because
        // C# forbids a yield inside a try that has a catch.
        VaultsClient? constructed = null;
        SecretsClient? constructedSecrets = null;
        Exception? clientFailure = null;

        try
        {
            constructed = factory.CreateSecretsManagement();
            constructedSecrets = factory.CreateSecrets();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clientFailure = ex;
        }

        if (constructed is null || constructedSecrets is null)
        {
            yield return DemoStep.Failed(
                "VaultsClient",
                clientFailure!,
                "new VaultsClient(endpoints.AuthenticationProvider())");

            yield break;
        }

        VaultsClient secretsManagement = constructed;
        SecretsClient secrets = constructedSecrets;

        string secretsManagementOrigin = secretsManagement.GetEndpoint().ToString().TrimEnd('/');
        string secretsOrigin = secrets.GetEndpoint().ToString().TrimEnd('/');

        // Unique per run, so two runs never collide and a leftover secret from a crashed run never
        // makes the next one fail.
        string secretName = $"flocilab-secret-{Guid.NewGuid():N}";
        bool secretCreated = false;
        string? secretId = null;

        DemoStep? secretCleanup;

        try
        {
            // A do/while(false), so the early exits below are `break` rather than `yield break` —
            // see VaultDemo.RunAsync's remarks for why: `yield break` would skip the cleanup step
            // the finally computes, silently dropping the news of whether a failed run's secret got
            // scheduled for deletion.
            do
            {
                yield return await RunStepAsync(
                    "ListSecrets — before",
                    $"GET {secretsManagementOrigin}/20180608/secrets?compartmentId={factory.CompartmentId}\nsecretsManagement.ListSecrets(new ListSecretsRequest {{ CompartmentId }})",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        ListSecretsResponse response = await secretsManagement.ListSecrets(
                            new ListSecretsRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);
                        IEnumerable<string> names = response.Items.Select(s => $"  {s.SecretName} ({s.Id})");

                        return $"{response.Items.Count} secret(s)\n" + string.Join('\n', names);
                    }).ConfigureAwait(false);

                // The vault and key are configuration, so an unset one is a setup problem rather
                // than anything the emulator did. Rendered as its own failed step naming what to
                // set, ahead of a CreateSecret that could only answer an opaque MissingParameter.
                if (!factory.TryGetTarget(out string vaultId, out string keyId, out string problem))
                {
                    yield return DemoStep.Failed(
                        "CreateSecret",
                        new InvalidOperationException(problem),
                        $"POST {secretsManagementOrigin}/20180608/secrets");

                    break;
                }

                yield return await RunStepAsync(
                    "CreateSecret",
                    $"POST {secretsManagementOrigin}/20180608/secrets\nContent-Type: application/json\n\n{{ \"secretName\": \"{secretName}\", \"vaultId\": \"{vaultId}\", \"keyId\": \"{keyId}\", \"secretContent\": {{ \"contentType\": \"BASE64\", \"stage\": \"CURRENT\" }} }}",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        // Set before the call, not after: if the POST lands but the response does
                        // not come back, the secret exists and cleanup has to know about it.
                        // Cleanup resolves it by name and treats an absent secret as a no-op, so
                        // claiming it early is free.
                        secretCreated = true;
                        CreateSecretResponse response = await secretsManagement.CreateSecret(
                            new CreateSecretRequest
                            {
                                CreateSecretDetails = new CreateSecretDetails
                                {
                                    CompartmentId = factory.CompartmentId,
                                    VaultId = vaultId,
                                    KeyId = keyId,
                                    SecretName = secretName,
                                    SecretContent = new Base64SecretContentDetails
                                    {
                                        Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(InitialValue)),
                                        Stage = SecretContentDetails.StageEnum.Current,
                                    },
                                },
                            },
                            cancellationToken: ct).ConfigureAwait(false);

                        secretId = response.Secret.Id;

                        return $"Secret {secretId}\n"
                            + $"  lifecycleState:       {response.Secret.LifecycleState}\n"
                            + $"  currentVersionNumber: {response.Secret.CurrentVersionNumber}";
                    }).ConfigureAwait(false);

                if (secretId is null)
                {
                    break;
                }

                yield return await RunStepAsync(
                    "GetSecretBundle",
                    $"GET {secretsOrigin}/20190301/secretbundles/{secretId}\nsecrets.GetSecretBundle(new GetSecretBundleRequest {{ SecretId }})",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        GetSecretBundleResponse response = await secrets.GetSecretBundle(
                            new GetSecretBundleRequest { SecretId = secretId }, cancellationToken: ct).ConfigureAwait(false);
                        string value = DecodeContent(response.SecretBundle.SecretBundleContent, "GetSecretBundle");

                        // A round-trip that returns something other than what was created did not
                        // round-trip. The lede promises this page shows what floci-oci actually
                        // answered, so a mismatch goes out red rather than a green badge over a
                        // broken read.
                        if (value != InitialValue)
                        {
                            throw new InvalidOperationException($"Payload was \"{value}\", not the value CreateSecret set.");
                        }

                        return $"versionNumber: {response.SecretBundle.VersionNumber}\nPayload: {value}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "UpdateSecret",
                    $"PUT {secretsManagementOrigin}/20180608/secrets/{secretId}\nContent-Type: application/json\n\n{{ \"secretContent\": {{ \"contentType\": \"BASE64\", \"stage\": \"CURRENT\" }} }}",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        UpdateSecretResponse response = await secretsManagement.UpdateSecret(
                            new UpdateSecretRequest
                            {
                                SecretId = secretId,
                                UpdateSecretDetails = new UpdateSecretDetails
                                {
                                    SecretContent = new Base64SecretContentDetails
                                    {
                                        Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(UpdatedValue)),
                                        Stage = SecretContentDetails.StageEnum.Current,
                                    },
                                },
                            },
                            cancellationToken: ct).ConfigureAwait(false);

                        return $"currentVersionNumber: {response.Secret.CurrentVersionNumber}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "GetSecretBundle — after update",
                    $"GET {secretsOrigin}/20190301/secretbundles/{secretId}\nsecrets.GetSecretBundle(new GetSecretBundleRequest {{ SecretId }})",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        GetSecretBundleResponse response = await secrets.GetSecretBundle(
                            new GetSecretBundleRequest { SecretId = secretId }, cancellationToken: ct).ConfigureAwait(false);
                        string value = DecodeContent(response.SecretBundle.SecretBundleContent, "GetSecretBundle");

                        // Same rule as the first GetSecretBundle: reading the CURRENT stage and
                        // still getting the old value means UpdateSecret did not actually take.
                        if (value != UpdatedValue)
                        {
                            throw new InvalidOperationException($"Payload was \"{value}\", not the value UpdateSecret set.");
                        }

                        return $"versionNumber: {response.SecretBundle.VersionNumber}\nPayload: {value}";
                    }).ConfigureAwait(false);
            }
            while (false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean compartment. Yielded below, since an iterator
            // may not yield from inside a finally. Nothing else is cleaned up: the vault and key
            // are configured infrastructure this sample did not create and must not remove.
            secretCleanup = secretCreated
                ? await ScheduleSecretDeletionAsync(secretsManagement, secretsManagementOrigin, factory.CompartmentId, secretName, secretId, ct).ConfigureAwait(false)
                : null;
        }

        if (secretCleanup is not null)
        {
            yield return secretCleanup;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> handles the transport cases but cannot see a status
    /// code hiding inside an <see cref="OciException"/>, which is where this SDK puts every answer
    /// the server gave. Same shape as <c>ObjectStorageDemo.Classify</c> and <c>VaultDemo.Classify</c>.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OciException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case OciException:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Real OCI marks the discriminated content union with a <c>"BASE64"</c> literal and this
    /// sample writes nothing else, so any other shape here means floci-oci answered with content
    /// this SDK cannot decode — a step should fail naming that, not NRE on a null cast.
    /// </summary>
    internal static string DecodeContent(SecretBundleContentDetails content, string operation)
    {
        if (content is not Base64SecretBundleContentDetails base64)
        {
            throw new InvalidOperationException(
                $"{operation} answered with {content?.GetType().Name ?? "no"} content, not the Base64 encoding this sample writes.");
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Content));
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real OCI Vault would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still schedules cleanup.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string Describe(Exception ex)
    {
        string message = ex is OciException oci
            ? $"{(int)oci.StatusCode} {oci.ServiceCode}: {FirstLine(oci.Message)}"
            : ex.Message;

        return ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? message
            : $"{message} ({FirstLine(ex.InnerException.Message)})";
    }

    private static string FirstLine(string message)
        => message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? message;

    /// <summary>
    /// Schedules the secret's deletion — the closest OCI Vault gets to "delete", real Vault never
    /// removes a secret synchronously. Resolves the secret by name via the emulator's server-side
    /// <c>name</c> filter (confirmed by curl 2026-09-02) rather than reusing <paramref
    /// name="secretId"/> in case <c>CreateSecret</c>'s own response never made it back. Uses
    /// <see cref="CancellationToken.None"/> — a run that was cancelled still has a secret to remove.
    /// </summary>
    private static async Task<DemoStep> ScheduleSecretDeletionAsync(VaultsClient secretsManagement, string origin, string compartmentId, string secretName, string? secretId, CancellationToken ct)
    {
        string request = $"POST {origin}/20180608/secrets/{{id}}/actions/scheduleDeletion\nsecretsManagement.ScheduleSecretDeletion(new ScheduleSecretDeletionRequest {{ SecretId }})";

        return await RunStepAsync("ScheduleSecretDeletion — cleanup", request, async () =>
        {
            string? resolvedId = secretId;

            // CreateSecret claims the name before it calls, so the secret may never have been
            // made. Asking the server rather than assuming: a lookup that finds nothing is proof
            // there is nothing to remove, which is a truthful green step (plan §14).
            if (resolvedId is null)
            {
                ListSecretsResponse lookup = await secretsManagement.ListSecrets(
                    new ListSecretsRequest { CompartmentId = compartmentId, Name = secretName }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                resolvedId = lookup.Items.FirstOrDefault(s => s.SecretName == secretName)?.Id;

                if (resolvedId is null)
                {
                    return "Nothing to remove — the secret was never created.";
                }
            }

            ScheduleSecretDeletionResponse response = await secretsManagement.ScheduleSecretDeletion(
                new ScheduleSecretDeletionRequest { SecretId = resolvedId, ScheduleSecretDeletionDetails = new ScheduleSecretDeletionDetails() },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return $"etag: {response.Etag}"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
