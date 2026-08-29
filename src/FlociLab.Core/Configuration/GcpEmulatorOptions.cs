namespace FlociLab.Core.Configuration;

public sealed class GcpEmulatorOptions : EmulatorOptions
{
    public GcpEmulatorOptions()
    {
        this.Endpoint = "http://localhost:4588";
        this.HealthPath = "/_floci-gcp/health";
    }

    public string ProjectId { get; set; } = "floci-local";
}
