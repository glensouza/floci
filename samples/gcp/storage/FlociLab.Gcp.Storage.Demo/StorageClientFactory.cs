using FlociLab.Core.Endpoints;
using Google.Cloud.Storage.V1;

namespace FlociLab.Gcp.Storage;

/// <summary>
/// The whole of the emulator-specific wiring for this sample, and a pleasant surprise: GCP is the
/// provider plan §7 calls hardest and §14 lists as the top risk, but Cloud Storage is the one
/// Google service that never touches gRPC. Two properties on the builder and it is done.
/// </summary>
public sealed class StorageClientFactory(GcpEndpoints endpoints)
{
    /// <summary>JSON API base, for showing the wire-level request alongside the SDK call.</summary>
    public string BaseUri => endpoints.StorageBaseUri;

    /// <summary>
    /// Where the SDK sends object *bytes*, which is not <see cref="BaseUri"/> — see
    /// <c>StorageDemo</c>'s upload step for why that distinction is worth showing.
    /// </summary>
    public string UploadUri => this.BaseUri.Replace("/storage/v1/", "/upload/storage/v1/", StringComparison.Ordinal);

    /// <summary>The project buckets are created in and listed under. Never validated by floci-gcp.</summary>
    public string ProjectId => endpoints.ProjectId;

    /// <summary>Whether the next <see cref="Create"/> targets floci-gcp or real Google Cloud.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public StorageClient Create()
    {
        // Real Google Cloud, and deliberately not a variant of the builder below with the endpoint
        // blanked out: both emulator lines are actively wrong here. UnauthenticatedAccess would
        // stop the client looking for the credentials it now genuinely needs, and BaseUri would
        // send it somewhere that is not Google. Everything downstream — StorageDemo, GcsObjectStore
        // — is reached identically either way, which is the claim the series makes out loud and
        // this branch is what makes it checkable rather than just asserted.
        if (!endpoints.UseEmulator)
        {
            return StorageClient.Create();
        }

        return new StorageClientBuilder
        {
            // The risk register's headline worry — that Google.Cloud.Storage.V1 would ignore a
            // custom BaseUri and there would be no way to reach the emulator short of hand-rolling
            // an HttpClient over the JSON API. It does not: verified end to end on 4.15.0 against
            // floci-gcp 0.7.0, create/upload/list/download/delete all land. The fallback in §14 is
            // not needed and that row can close.
            //
            // The trailing slash matters. The SDK appends relative paths ("b", "b/{bucket}/o") to
            // this, so dropping it addresses /storage/b instead of /storage/v1/b.
            BaseUri = endpoints.StorageBaseUri,

            // No credentials anywhere in the emulator, and this is what stops the SDK looking for
            // them. Without it the builder walks the ADC chain — GOOGLE_APPLICATION_CREDENTIALS,
            // then gcloud's well-known file, then the GCE metadata server — and fails on a
            // developer machine that has never seen gcloud, with an error about credentials rather
            // than about the endpoint. It is also the honest description of the emulator: it does
            // not check. Against real GCP this line is not redundant, it is wrong — see above.
            UnauthenticatedAccess = true,

            // Deliberately NOT setting EmulatorDetection. It does work here — see the class remarks
            // on StorageDemo — but it is driven by a process-wide environment variable, and a web
            // app that binds its endpoint from configuration should not be reaching for one.
        }.Build();
    }

    // No retry knob, which is the one place this sample departs from its S3 and Blob siblings.
    // Both of those turn retries off (MaxErrorRetry / MaxRetries = 0) so a page whose job is to
    // show "the emulator is down" says so promptly and the request shown beside each step is the
    // only one that went out. Google.Apis' handler already behaves that way for a refused
    // connection: measured against a dead port, NumTries = 1 and the default are the same ~2.0 s,
    // and that 2.0 s is the operating system's own connect timeout — a raw HttpClient to the same
    // port costs exactly as much. There is nothing to turn off.
}
