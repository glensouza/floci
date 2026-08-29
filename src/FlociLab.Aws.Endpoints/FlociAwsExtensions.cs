using Amazon.Runtime;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws;

/// <summary>
/// One shape covers all ~82 AWS services, which is why AWS is the easy provider (plan §7). A
/// sample's client factory is then two lines:
///
/// <code>
/// AmazonS3Config config = new AmazonS3Config { ForcePathStyle = true }.ForFloci(endpoints);
/// return new AmazonS3Client(endpoints.Credentials(), config);
/// </code>
/// </summary>
public static class FlociAwsExtensions
{
    /// <summary>
    /// Points any <see cref="ClientConfig"/> at the emulator. Service-specific knobs stay with the
    /// sample — S3 additionally needs <c>ForcePathStyle = true</c>.
    /// </summary>
    public static TConfig ForFloci<TConfig>(this TConfig config, AwsEndpoints endpoints)
        where TConfig : ClientConfig
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(endpoints);

        config.ServiceURL = endpoints.ServiceUrl;
        config.AuthenticationRegion = endpoints.Region;
        config.UseHttp = endpoints.UseHttp;

        return config;
    }

    /// <summary>
    /// Floci parses SigV4 but does not verify it, so any well-formed pair works. These are the
    /// same "test"/"test" credentials the README's AWS CLI profile uses.
    /// </summary>
    public static AWSCredentials Credentials(this AwsEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return new BasicAWSCredentials(endpoints.AccessKeyId, endpoints.SecretAccessKey);
    }
}
