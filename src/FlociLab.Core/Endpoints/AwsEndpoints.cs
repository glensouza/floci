using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Endpoints;

/// <summary>
/// Everything an AWS SDK client needs to talk to the emulator, with no reference to the AWS SDK
/// itself — that stays in the sample, which is the only project allowed to name a cloud package
/// (docs/BLAZOR-PLAN.md §3, constraint 1). A sample's client factory is then three lines:
///
/// <code>
/// var cfg = new AmazonS3Config
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
    private readonly AwsEmulatorOptions _options = options.Value.Aws;

    /// <summary>Goes straight into <c>ClientConfig.ServiceURL</c>.</summary>
    public string ServiceUrl => _options.Endpoint;

    /// <summary><c>ClientConfig.AuthenticationRegion</c> — SigV4 is parsed, not verified.</summary>
    public string Region => _options.Region;

    /// <summary><c>ClientConfig.UseHttp</c>. True unless the emulator was started with TLS.</summary>
    public bool UseHttp => ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    public string AccessKeyId => _options.AccessKeyId;

    public string SecretAccessKey => _options.SecretAccessKey;
}
