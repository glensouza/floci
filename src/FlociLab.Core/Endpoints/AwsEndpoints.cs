using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Endpoints;

/// <summary>
/// Everything an AWS SDK client needs to talk to the emulator, with no reference to the AWS SDK
/// itself — that stays in the sample, which is the only project allowed to name a cloud package
/// (docs/BLAZOR-PLAN.md §3, constraint 1). A sample's client factory is then three lines:
///
/// <code>
/// AmazonS3Config cfg = new()
/// {
///     ServiceURL = endpoints.ServiceUrl,
///     AuthenticationRegion = endpoints.Region,
///     UseHttp = endpoints.UseHttp,
///     ForcePathStyle = true,              // S3 only
/// };
/// return new AmazonS3Client(new BasicAWSCredentials(endpoints.AccessKeyId, endpoints.SecretAccessKey), cfg);
/// </code>
///
/// One shape covers all ~82 services, which is why AWS is the easy provider here (plan §7).
/// </summary>
public sealed class AwsEndpoints(IOptions<FlociOptions> options)
{
    private readonly AwsEmulatorOptions emulatorOptions = options.Value.Aws;

    /// <summary>
    /// False targets real AWS: the factory drops <c>ServiceURL</c>, <c>ForcePathStyle</c> and the
    /// static credentials, and lets the SDK use its own resolution and retry defaults.
    /// </summary>
    public bool UseEmulator => this.emulatorOptions.UseEmulator;

    /// <summary>Goes straight into <c>ClientConfig.ServiceURL</c>.</summary>
    public string ServiceUrl => this.emulatorOptions.Endpoint;

    /// <summary><c>ClientConfig.AuthenticationRegion</c> — SigV4 is parsed, not verified.</summary>
    public string Region => this.emulatorOptions.Region;

    /// <summary><c>ClientConfig.UseHttp</c>. True unless the emulator was started with TLS.</summary>
    public bool UseHttp => this.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    public string AccessKeyId => this.emulatorOptions.AccessKeyId;

    public string SecretAccessKey => this.emulatorOptions.SecretAccessKey;
}
