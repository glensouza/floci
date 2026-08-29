namespace FlociLab.Core.Configuration;

/// <summary>What every emulator has: somewhere to reach it, and somewhere to ask if it is alive.</summary>
public abstract class EmulatorOptions
{
    /// <summary>Base URL of the emulator, e.g. http://localhost:4566.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// The image's health endpoint. NOT uniform across the four images, despite the README:
    /// floci and floci-az serve /_floci/health, while floci-gcp and floci-oci serve
    /// /_floci-gcp/health and /_floci-oci/health and return 404 on /_floci/health. Confirmed
    /// against the containers' own HEALTHCHECK commands, 2026-08-28.
    /// </summary>
    public string HealthPath { get; set; } = "/_floci/health";
}
