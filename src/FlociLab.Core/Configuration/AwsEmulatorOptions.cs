namespace FlociLab.Core.Configuration;

public sealed class AwsEmulatorOptions : EmulatorOptions
{
    public AwsEmulatorOptions() => this.Endpoint = "http://127.0.0.1:4566";

    public string Region { get; set; } = "us-east-1";

    /// <summary>Floci parses credentials but does not verify them; "test"/"test" is the convention.</summary>
    public string AccessKeyId { get; set; } = "test";

    public string SecretAccessKey { get; set; } = "test";
}
