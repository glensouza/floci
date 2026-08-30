namespace FlociLab.Core.Configuration;

/// <summary>What every emulator has: somewhere to reach it, and somewhere to ask if it is alive.</summary>
public abstract class EmulatorOptions
{
    /// <summary>Base URL of the emulator, e.g. http://localhost:4566.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// Whether this provider's samples target the emulator or the real cloud. Default <c>true</c>,
    /// so nothing runs up a bill by accident and the app still works with no configuration at all.
    ///
    /// <para>
    /// This exists because the series' headline claim — that a sample is ordinary SDK code you
    /// could ship — is only checkable if the same assembly can actually be pointed at the real
    /// service. It could not before: the emulator knobs were unconditional, and two of them are
    /// not merely redundant against real cloud but actively wrong. <c>UnauthenticatedAccess</c>
    /// stops the GCS client looking for credentials it would need, and <c>ForcePathStyle</c> is
    /// not how real S3 addresses new buckets. Set this to <c>false</c> and each factory builds its
    /// client the production way instead: no endpoint override, the SDK's own credential chain,
    /// and the SDK's own retry defaults.
    /// </para>
    ///
    /// <para>
    /// Per provider rather than global, because you will rarely hold live accounts for all four at
    /// once. Nothing in the test suite sets it — CI stays emulator-only and needs no secrets.
    /// </para>
    /// </summary>
    public bool UseEmulator { get; set; } = true;

    /// <summary>
    /// The image's health endpoint. NOT uniform across the four images, despite the README:
    /// floci and floci-az serve /_floci/health, while floci-gcp and floci-oci serve
    /// /_floci-gcp/health and /_floci-oci/health and return 404 on /_floci/health. Confirmed
    /// against the containers' own HEALTHCHECK commands, 2026-08-28.
    /// </summary>
    public string HealthPath { get; set; } = "/_floci/health";
}
