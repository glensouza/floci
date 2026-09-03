using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using FlociLab.Core;
using Oci.Common.Model;
using Oci.KeymanagementService;
using Oci.KeymanagementService.Models;
using VaultModel = Oci.KeymanagementService.Models.Vault;
using Oci.KeymanagementService.Requests;
using Oci.KeymanagementService.Responses;

namespace FlociLab.Oci.Vault;

/// <summary>
/// OCI Vault + KMS against floci-oci. Ordinary OCI.DotNetSDK.Keymanagement code — the only
/// emulator-aware lines in the sample are in <see cref="VaultClientFactory"/>.
///
/// <para>
/// Real OCI Vault splits its API across three clients: <see cref="KmsVaultClient"/> is the control
/// plane (create/list a vault, schedule its deletion), while <see cref="KmsManagementClient"/>
/// (create/list/delete keys) and <see cref="KmsCryptoClient"/> (encrypt/decrypt) are addressed at
/// the per-vault <c>managementEndpoint</c> / <c>cryptoEndpoint</c> a real tenancy returns from
/// <c>CreateVault</c>. floci-oci builds those values from its own configuration rather than from
/// the address the caller reached, so it is only correct by coincidence — see
/// <see cref="VaultClientFactory.CreateManagement"/> for what it actually answers and why this
/// sample overrides it.
/// </para>
///
/// <para>
/// A vault, once created, is a real resource — creation is synchronous against floci-oci but real
/// OCI Vault answers <c>CREATING</c> before <c>ACTIVE</c>, so this polls
/// <c>KmsVaultClient.Waiters.ForVault</c> rather than trusting the create response's state, the
/// same shape <c>QueueDemo</c> needs for its work requests.
/// </para>
/// </summary>
public sealed class VaultDemo(VaultClientFactory factory) : IServiceDemo
{
    private const string Plaintext = "Hello from FlociLab.";

    public string Provider => CloudProvider.Oci;

    public string Slug => "vault";

    public string DisplayName => "Vault";

    public string Category => "Security";

    public string Route => "/oci/vault";

    /// <summary>ListVaults — one request, no state, and the cheapest call the service answers.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            KmsVaultClient vault = factory.CreateVault();
            ListVaultsResponse response = await vault.ListVaults(
                new ListVaultsRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListVaults returned {response.Items.Count} vault(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Building the vault client can itself fail: the real-cloud branch of the factory refuses
        // a run that would create a vault in the lab's synthetic compartment. That has to become a
        // failed step like any other — an iterator that throws on the first MoveNextAsync takes
        // down the circuit instead of rendering the reason. Caught here and yielded below, because
        // C# forbids a yield inside a try that has a catch.
        KmsVaultClient? constructed = null;
        Exception? clientFailure = null;

        try
        {
            constructed = factory.CreateVault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clientFailure = ex;
        }

        if (constructed is null)
        {
            yield return DemoStep.Failed(
                "KmsVaultClient",
                clientFailure!,
                "new KmsVaultClient(endpoints.AuthenticationProvider())");

            yield break;
        }

        KmsVaultClient vault = constructed;

        // Asked of the client rather than taken from the factory — see ObjectStorageDemo.RunAsync
        // for why: in emulator mode ForFloci has set both the endpoint and the realm template to
        // the emulator, so this is the emulator; in real-cloud mode this is whatever the SDK
        // resolved from the region.
        string origin = vault.GetEndpoint().ToString().TrimEnd('/');

        // Unique per run, so two runs never collide and a leftover vault from a crashed run never
        // makes the next one fail.
        string vaultName = $"flocilab-vault-{Guid.NewGuid():N}";
        bool vaultCreated = false;
        string? vaultId = null;
        string? managementEndpoint = null;
        string? cryptoEndpoint = null;

        KmsManagementClient? management = null;
        KmsCryptoClient? crypto = null;
        string? managementOrigin = null;
        string? keyId = null;

        DemoStep? keyCleanup;
        DemoStep? vaultCleanup;

        try
        {
            // A do/while(false), so the early exits below are `break` rather than `yield break`.
            // The difference matters: `yield break` still runs the finally — cleanup does happen —
            // but it then terminates the iterator, so the cleanup steps the finally just computed
            // are never yielded. A run that failed at CreateKey would silently drop the news of
            // whether its vault got scheduled for deletion, which is the one thing a page built to
            // show the failures must not do. `break` leaves the loop, the finally runs, and the two
            // yields after it are reached.
            do
            {
                yield return await RunStepAsync(
                    "ListVaults — before",
                    $"GET {origin}/20180608/vaults?compartmentId={factory.CompartmentId}\nvault.ListVaults(new ListVaultsRequest {{ CompartmentId }})",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        ListVaultsResponse response = await vault.ListVaults(
                            new ListVaultsRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);
                        IEnumerable<string> names = response.Items.Select(v => $"  {v.DisplayName} ({v.Id})");

                        return $"{response.Items.Count} vault(s)\n" + string.Join('\n', names);
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateVault",
                    $"POST {origin}/20180608/vaults\nContent-Type: application/json\n\n{{ \"displayName\": \"{vaultName}\", \"compartmentId\": \"{factory.CompartmentId}\", \"vaultType\": \"DEFAULT\" }}",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        // Set before the call, not after: if the POST lands but the response does not
                        // come back, the vault exists and cleanup has to know about it. Cleanup treats
                        // an absent vault as a no-op, so claiming it early is free.
                        vaultCreated = true;
                        CreateVaultResponse createResponse = await vault.CreateVault(
                            new CreateVaultRequest
                            {
                                CreateVaultDetails = new CreateVaultDetails
                                {
                                    CompartmentId = factory.CompartmentId,
                                    DisplayName = vaultName,
                                    VaultType = CreateVaultDetails.VaultTypeEnum.Default,
                                },
                            },
                            cancellationToken: ct).ConfigureAwait(false);

                        vaultId = createResponse.Vault.Id;
                        managementEndpoint = createResponse.Vault.ManagementEndpoint;
                        cryptoEndpoint = createResponse.Vault.CryptoEndpoint;

                        GetVaultResponse finished = await vault.Waiters
                            .ForVault(new GetVaultRequest { VaultId = vaultId }, VaultModel.LifecycleStateEnum.Active)
                            .ExecuteAsync().ConfigureAwait(false);

                        if (finished.Vault.LifecycleState != VaultModel.LifecycleStateEnum.Active)
                        {
                            throw new InvalidOperationException(
                                $"Vault {vaultId} finished as {finished.Vault.LifecycleState}, not ACTIVE.");
                        }

                        return $"Vault {vaultId}\n"
                            + $"  lifecycleState:     {finished.Vault.LifecycleState}\n"
                            + $"  managementEndpoint: {managementEndpoint} (reported by floci-oci from its own config, not from this request — see CreateKey below)\n"
                            + $"  cryptoEndpoint:     {cryptoEndpoint}";
                    }).ConfigureAwait(false);

                // CreateVault failed, so there is nothing to address. Stop rather than emitting three
                // more steps whose only news is a null vault id.
                if (vaultId is null || managementEndpoint is null || cryptoEndpoint is null)
                {
                    break;
                }

                // Guarded exactly like the vault client above, and for the same reason: a throw here
                // would escape RunAsync and take the circuit down instead of rendering a failed step.
                // Real-cloud mode is where this bites — that branch builds a
                // ConfigFileAuthenticationDetailsProvider and hands CreateVault's own endpoints to the
                // client constructors, so a bad profile throws here rather than at a call site.
                string? cryptoOrigin = null;
                Exception? planeFailure = null;

                try
                {
                    management = factory.CreateManagement(managementEndpoint);
                    managementOrigin = management.GetEndpoint().ToString().TrimEnd('/');
                    crypto = factory.CreateCrypto(cryptoEndpoint);
                    cryptoOrigin = crypto.GetEndpoint().ToString().TrimEnd('/');
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    planeFailure = ex;
                }

                if (management is null || crypto is null || managementOrigin is null || cryptoOrigin is null)
                {
                    yield return DemoStep.Failed(
                        "KmsManagementClient / KmsCryptoClient",
                        planeFailure!,
                        $"new KmsManagementClient(auth, endpoint: \"{managementEndpoint}\")");

                    break;
                }

                KmsManagementClient managementClient = management;
                KmsCryptoClient cryptoClient = crypto;
                byte[] ciphertext = [];

                yield return await RunStepAsync(
                    "CreateKey",
                    $"POST {managementOrigin}/20180608/keys\nContent-Type: application/json\n\n{{ \"displayName\": \"flocilab-key\", \"keyShape\": {{ \"algorithm\": \"AES\", \"length\": 32 }}, \"protectionMode\": \"SOFTWARE\" }}",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        CreateKeyResponse response = await managementClient.CreateKey(
                            new CreateKeyRequest
                            {
                                CreateKeyDetails = new CreateKeyDetails
                                {
                                    CompartmentId = factory.CompartmentId,
                                    DisplayName = $"flocilab-key-{Guid.NewGuid():N}",
                                    KeyShape = new KeyShape { Algorithm = KeyShape.AlgorithmEnum.Aes, Length = 32 },
                                    ProtectionMode = CreateKeyDetails.ProtectionModeEnum.Software,
                                },
                            },
                            cancellationToken: ct).ConfigureAwait(false);

                        keyId = response.Key.Id;

                        return $"Key {keyId}\n  lifecycleState: {response.Key.LifecycleState}";
                    }).ConfigureAwait(false);

                if (keyId is null)
                {
                    break;
                }

                yield return await RunStepAsync(
                    "Encrypt",
                    $"POST {cryptoOrigin}/20180608/encrypt\ncrypto.Encrypt(new EncryptDataDetails {{ KeyId, Plaintext: \"{Plaintext}\" }})",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        EncryptResponse response = await cryptoClient.Encrypt(
                            new EncryptRequest
                            {
                                EncryptDataDetails = new EncryptDataDetails
                                {
                                    KeyId = keyId,
                                    Plaintext = Convert.ToBase64String(Encoding.UTF8.GetBytes(Plaintext)),
                                },
                            },
                            cancellationToken: ct).ConfigureAwait(false);

                        ciphertext = Convert.FromBase64String(response.EncryptedData.Ciphertext);

                        // Both checks the reference KmsDemo applies, and for the same reason: the step
                        // has to fail at the operation that misbehaved rather than one step later. An
                        // empty ciphertext is not caught by the plaintext comparison below — it simply
                        // differs from the plaintext — so it would render green as "0 byte(s)" and only
                        // surface as a baffling Decrypt error.
                        if (ciphertext.Length == 0)
                        {
                            throw new InvalidOperationException("Encrypt returned an empty ciphertext; nothing was encrypted.");
                        }

                        // A no-op that merely echoes the plaintext back would round-trip perfectly in
                        // Decrypt below, so this checks the ciphertext is not the plaintext itself —
                        // same rule GcpKmsDemo's Encrypt step applies.
                        if (ciphertext.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(Plaintext)))
                        {
                            throw new InvalidOperationException(
                                $"Encrypt returned {ciphertext.Length} byte(s) that are the plaintext itself; nothing was encrypted.");
                        }

                        return $"{ciphertext.Length} byte(s) of ciphertext";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "Decrypt",
                    $"POST {cryptoOrigin}/20180608/decrypt\ncrypto.Decrypt(new DecryptDataDetails {{ KeyId, Ciphertext: <{ciphertext.Length} bytes> }})",
                    async () =>
                    {
                        ct.ThrowIfCancellationRequested();

                        DecryptResponse response = await cryptoClient.Decrypt(
                            new DecryptRequest
                            {
                                DecryptDataDetails = new DecryptDataDetails { KeyId = keyId, Ciphertext = Convert.ToBase64String(ciphertext) },
                            },
                            cancellationToken: ct).ConfigureAwait(false);

                        string decrypted = Encoding.UTF8.GetString(Convert.FromBase64String(response.DecryptedData.Plaintext));

                        if (decrypted != Plaintext)
                        {
                            throw new InvalidOperationException($"Decrypted \"{decrypted}\" does not match the plaintext that was encrypted.");
                        }

                        return $"Plaintext: {decrypted}";
                    }).ConfigureAwait(false);
            }
            while (false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean compartment. The steps are yielded below — an
            // iterator may not yield from inside a finally.
            keyCleanup = keyId is not null && management is not null
                ? await ScheduleKeyDeletionAsync(management, managementOrigin!, keyId, ct).ConfigureAwait(false)
                : null;
            vaultCleanup = vaultCreated
                ? await ScheduleVaultDeletionAsync(vault, origin, factory.CompartmentId, vaultName, vaultId, ct).ConfigureAwait(false)
                : null;
            management?.Dispose();
            crypto?.Dispose();
        }

        if (keyCleanup is not null)
        {
            yield return keyCleanup;
        }

        if (vaultCleanup is not null)
        {
            yield return vaultCleanup;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> handles the transport cases but cannot see a status
    /// code hiding inside an <see cref="OciException"/>, which is where this SDK puts every answer
    /// the server gave. Same shape as <c>ObjectStorageDemo.Classify</c> and <c>QueueDemo.Classify</c>.
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
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
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
    /// Schedules the key's deletion — the closest OCI Vault gets to "delete" (see
    /// <see cref="ScheduleVaultDeletionAsync"/> for the vault's own, real Vault never deletes
    /// anything synchronously). Uses <see cref="CancellationToken.None"/> — a run that was
    /// cancelled still has a key to schedule.
    /// </summary>
    private static async Task<DemoStep> ScheduleKeyDeletionAsync(KmsManagementClient management, string origin, string keyId, CancellationToken ct)
    {
        string request = $"POST {origin}/20180608/keys/{keyId}/actions/scheduleDeletion\nmanagement.ScheduleKeyDeletion(new ScheduleKeyDeletionRequest {{ KeyId }})";

        return await RunStepAsync("ScheduleKeyDeletion — cleanup", request, async () =>
        {
            ScheduleKeyDeletionResponse response = await management.ScheduleKeyDeletion(
                new ScheduleKeyDeletionRequest { KeyId = keyId, ScheduleKeyDeletionDetails = new ScheduleKeyDeletionDetails() },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return $"lifecycleState: {response.Key.LifecycleState}, timeOfDeletion: {response.Key.TimeOfDeletion:u}"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleanup. Resolves the vault by name rather than reusing <paramref name="vaultId"/> in case
    /// CreateVault's own response never made it back (a dropped connection after the request landed
    /// leaves the vault created server-side, and the name is all cleanup has). Real OCI Vault never
    /// actually deletes a vault on request — this schedules it for deletion 7–30 days out, real
    /// Vault behaviour rather than a floci quirk, which is why the postcondition checked here is the
    /// returned lifecycle state rather than the vault being gone. Uses
    /// <see cref="CancellationToken.None"/> — a run that was cancelled still has a vault to remove.
    /// </summary>
    private static async Task<DemoStep> ScheduleVaultDeletionAsync(KmsVaultClient vault, string origin, string compartmentId, string vaultName, string? vaultId, CancellationToken ct)
    {
        string request = $"POST {origin}/20180608/vaults/{{id}}/actions/scheduleDeletion\nvault.ScheduleVaultDeletion(new ScheduleVaultDeletionRequest {{ VaultId }})";

        return await RunStepAsync("ScheduleVaultDeletion — cleanup", request, async () =>
        {
            string? resolvedId = vaultId;

            // CreateVault claims the name before it calls, so the vault may never have been made —
            // that is a clean run to finish, not a cleanup failure worth showing in red.
            if (resolvedId is null)
            {
                ListVaultsResponse lookup = await vault.ListVaults(
                    new ListVaultsRequest { CompartmentId = compartmentId }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                resolvedId = lookup.Items.FirstOrDefault(v => v.DisplayName == vaultName)?.Id;

                if (resolvedId is null)
                {
                    return "Nothing to remove — the vault was never created.";
                }
            }

            ScheduleVaultDeletionResponse response = await vault.ScheduleVaultDeletion(
                new ScheduleVaultDeletionRequest { VaultId = resolvedId, ScheduleVaultDeletionDetails = new ScheduleVaultDeletionDetails() },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return $"lifecycleState: {response.Vault.LifecycleState}, timeOfDeletion: {response.Vault.TimeOfDeletion:u}"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
