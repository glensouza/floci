using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Oci.Common.Auth;
using Oci.SecretsService;
using Oci.VaultService;

namespace FlociLab.Oci.Secrets;

/// <summary>
/// The emulator-specific wiring for this sample. OCI Vault Secrets is two clients, because real OCI
/// splits it into two planes that ship as separate SDK packages: secret CRUD goes through
/// <see cref="VaultsClient"/> (<c>OCI.DotNetSDK.Vault</c>) and reading a secret's decrypted value
/// through <see cref="SecretsClient"/> (<c>OCI.DotNetSDK.Secrets</c>). Neither is host-routed per
/// vault the way <c>KmsManagementClient</c>/<c>KmsCryptoClient</c> are — both resolve from the
/// region, confirmed by curl against a fresh floci-oci 0.3.0 container: <c>POST /20180608/secrets</c>
/// and <c>GET /20190301/secretbundles/{id}</c> both answer directly on the emulator's one address,
/// 2026-09-02.
///
/// <para>
/// The vault and key a secret needs are configuration (<see cref="VaultId"/>, <see cref="KeyId"/>),
/// not something this sample creates — see the remarks on the <c>.csproj</c> for why that is a
/// package boundary rather than a convenience.
/// </para>
/// </summary>
public sealed class SecretsClientFactory(OciEndpoints endpoints) : IDisposable
{
    private readonly Lock @lock = new();

    private VaultsClient? secretsManagementClient;
    private SecretsClient? secretsClient;

    /// <summary>Only meaningful in emulator mode — see <c>ObjectStorageClientFactory.Endpoint</c>.</summary>
    public string? Endpoint => endpoints.UseEmulator ? endpoints.Endpoint : null;

    public string Region => endpoints.Region;

    public string CompartmentId => endpoints.TenancyId;

    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>The vault a secret is created in, or <see langword="null"/> when unconfigured.</summary>
    public string? VaultId => endpoints.VaultId;

    /// <summary>The master encryption key a secret is encrypted with, or <see langword="null"/> when unconfigured.</summary>
    public string? KeyId => endpoints.KeyId;

    /// <summary>
    /// The vault and key OCIDs, or an explanation of which one is missing. Returned rather than
    /// thrown so the demo and the capability can each surface it in their own shape — a failed step
    /// on the page, an exception from the comparison column.
    /// </summary>
    internal bool TryGetTarget(out string vaultId, out string keyId, out string problem)
    {
        vaultId = endpoints.VaultId ?? "";
        keyId = endpoints.KeyId ?? "";

        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(vaultId))
        {
            missing.Add("Floci:Oci:VaultId");
        }

        if (string.IsNullOrWhiteSpace(keyId))
        {
            missing.Add("Floci:Oci:KeyId");
        }

        if (missing.Count == 0)
        {
            problem = "";

            return true;
        }

        // Named in full, with the fix, because this is the one failure a reader hits before the
        // sample has ever worked for them — an opaque message here reads as a broken demo.
        problem = $"{string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} unset. "
            + "CreateSecret requires a vault and a master encryption key, and this sample "
            + "deliberately does not carry OCI.DotNetSDK.Keymanagement to create them (see the "
            + ".csproj). Run the OCI Vault page once — it creates both and prints their OCIDs — "
            + "then set them in configuration.";

        return false;
    }

    /// <summary>The secret CRUD client (create/update/list/schedule-deletion) — region-resolved, not per-vault.</summary>
    public VaultsClient CreateSecretsManagement()
    {
        lock (this.@lock)
        {
            return this.secretsManagementClient ??= this.BuildSecretsManagement();
        }
    }

    /// <summary>The data-plane client — the only one that can read a secret's actual value. Region-resolved, not per-vault.</summary>
    public SecretsClient CreateSecrets()
    {
        lock (this.@lock)
        {
            return this.secretsClient ??= this.BuildSecrets();
        }
    }

    /// <summary>Disposes the two shared clients. Called by the container — the factory is a singleton.</summary>
    public void Dispose()
    {
        lock (this.@lock)
        {
            this.secretsManagementClient?.Dispose();
            this.secretsManagementClient = null;
            this.secretsClient?.Dispose();
            this.secretsClient = null;
        }
    }

    private VaultsClient BuildSecretsManagement()
    {
        // Real Oracle Cloud — see ObjectStorageClientFactory.Build for why this branch refuses a
        // run against the lab's synthetic tenancy rather than quietly writing secrets into it.
        this.GuardRealCloud();

        if (!endpoints.UseEmulator)
        {
            return new VaultsClient(new ConfigFileAuthenticationDetailsProvider("DEFAULT"));
        }

        VaultsClient client = new(endpoints.AuthenticationProvider());

        return client.ForFloci(endpoints);
    }

    private SecretsClient BuildSecrets()
    {
        this.GuardRealCloud();

        if (!endpoints.UseEmulator)
        {
            return new SecretsClient(new ConfigFileAuthenticationDetailsProvider("DEFAULT"));
        }

        SecretsClient client = new(endpoints.AuthenticationProvider());

        return client.ForFloci(endpoints);
    }

    private void GuardRealCloud()
    {
        if (endpoints.UseEmulator)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(endpoints.ConfiguredTenancyId)
            || endpoints.ConfiguredTenancyId == OciEmulatorOptions.DefaultTenancyId)
        {
            throw new InvalidOperationException(
                "Floci:Oci:UseEmulator is false, so this targets real Oracle Cloud, but "
                + "Floci:Oci:TenancyId is unset or still the lab's synthetic default. Set it "
                + "explicitly to the OCID of the compartment the secret should live in — "
                + "FLOCI_OCI_DEFAULT_TENANCY_ID does not count, it configures the emulator.");
        }
    }
}
