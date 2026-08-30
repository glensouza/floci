using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Oci.Common.Auth;
using Oci.ObjectstorageService;

namespace FlociLab.Oci.ObjectStorage;

/// <summary>
/// The whole of the emulator-specific wiring for this sample, and the one place in the repo where
/// getting it wrong does not fail loudly — see <c>FlociOciExtensions.ForFloci</c>. Three lines:
/// an authentication provider built from a key generated at startup, the endpoint, and the realm
/// template the endpoint alone does not override.
/// </summary>
public sealed class ObjectStorageClientFactory(OciEndpoints endpoints) : IDisposable
{
    private readonly Lock @lock = new();

    private ObjectStorageClient? client;

    /// <summary>
    /// What requests are actually addressed to. Only meaningful in emulator mode: the real-cloud
    /// branch applies no endpoint override at all and lets the SDK resolve one from the region, so
    /// reporting the configured emulator address there would put
    /// <c>http://localhost:4599</c> underneath a page that says "REAL Oracle Cloud" — the same
    /// class of quiet lie as <c>GetEndpoint()</c>, on a page whose whole subject is that lie.
    /// </summary>
    public string? Endpoint => endpoints.UseEmulator ? endpoints.Endpoint : null;

    public string Region => endpoints.Region;

    /// <summary>
    /// Buckets live in a compartment, not in an account. The tenancy OCID *is* the root
    /// compartment's OCID in real OCI, which is why one value serves both here.
    /// </summary>
    public string CompartmentId => endpoints.TenancyId;

    /// <summary>Whether the next <see cref="Create"/> targets floci-oci or real Oracle Cloud.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// One client for the process, built on first use — which is what production would do, and
    /// now what this does too. Same reasoning as the GCS factory, and the same measurement
    /// behind it: a fresh client per call meant a fresh connection pool per call, and the
    /// comparison page charged OCI ~2 s for every operation because of it. See
    /// <c>EmulatorOptions.Endpoint</c> for why a new pool on loopback is so expensive, and
    /// <c>StorageClientFactory.Create</c> for why the "configuration might have changed between
    /// runs" argument for rebuilding never actually held.
    /// </summary>
    public ObjectStorageClient Create()
    {
        lock (this.@lock)
        {
            return this.client ??= this.Build();
        }
    }

    /// <summary>Disposes the shared client. Called by the container — the factory is a singleton.</summary>
    public void Dispose()
    {
        lock (this.@lock)
        {
            this.client?.Dispose();
            this.client = null;
        }
    }

    private ObjectStorageClient Build()
    {
        // Real Oracle Cloud. Deliberately not the emulator branch with the endpoint blanked out:
        // the generated key is not a credential Oracle has ever seen, and the endpoint override
        // would send the call somewhere that is not OCI. What is identical either way is
        // everything downstream — ObjectStorageDemo, OciObjectStore, the page — which is the claim
        // the series makes out loud, and this branch is what makes it checkable.
        if (!endpoints.UseEmulator)
        {
            // Refusing rather than quietly creating buckets in a compartment nobody chose. The
            // lab's synthetic tenancy is a well-formed OCID that real OCI will simply reject, so
            // the failure without this check is a confusing 404 rather than an obvious mistake.
            //
            // Deliberately ConfiguredTenancyId, not TenancyId: the latter also accepts
            // FLOCI_OCI_DEFAULT_TENANCY_ID, which is how the AppHost and the tests line a
            // container up with the samples. A developer who exported it to match a hand-started
            // floci-oci would otherwise satisfy this check with a value that is still synthetic,
            // and the guard would wave the run through to real Oracle Cloud.
            if (string.IsNullOrWhiteSpace(endpoints.ConfiguredTenancyId)
                || endpoints.ConfiguredTenancyId == OciEmulatorOptions.DefaultTenancyId)
            {
                throw new InvalidOperationException(
                    "Floci:Oci:UseEmulator is false, so this targets real Oracle Cloud, but "
                    + "Floci:Oci:TenancyId is unset or still the lab's synthetic default. Set it "
                    + "explicitly to the OCID of the compartment the buckets should live in — "
                    + "FLOCI_OCI_DEFAULT_TENANCY_ID does not count, it configures the emulator.");
            }

            // Everything real OCI needs — user, fingerprint, key, region — comes from the DEFAULT
            // profile in ~/.oci/config, which is where the OCI CLI already put it. No endpoint
            // override: the region in that profile picks the right one.
            return new ObjectStorageClient(new ConfigFileAuthenticationDetailsProvider("DEFAULT"));
        }

        ObjectStorageClient client = new(endpoints.AuthenticationProvider());

        // Not client.SetEndpoint(...). That is ignored, silently, in favour of a realm template
        // built from the provider's region, and the call lands on real Oracle Cloud. ForFloci
        // sets both. This is the single most important line in the OCI half of the repo.
        return client.ForFloci(endpoints);
    }
}
