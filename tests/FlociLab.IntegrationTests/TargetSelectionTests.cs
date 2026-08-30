using FlociLab.Aws.S3;
using FlociLab.Azure.Blob;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Gcp.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// Guards the one setting in this repo that can cost real money. <see cref="EmulatorOptions.UseEmulator"/>
/// decides whether a sample builds an emulator client or a production one, and every one of these
/// asserts the safe direction: absent configuration means the emulator.
///
/// <para>
/// No container and no credentials — this is all construction-time behaviour, which is the point.
/// A test that needed a real cloud account to prove the default is safe would never run.
/// </para>
/// </summary>
public sealed class TargetSelectionTests
{
    [Fact]
    public void UseEmulator_Defaults_To_True_For_Every_Provider()
    {
        FlociOptions options = new();

        Assert.True(options.Aws.UseEmulator);
        Assert.True(options.Azure.UseEmulator);
        Assert.True(options.Gcp.UseEmulator);
        Assert.True(options.Oci.UseEmulator);
    }

    /// <summary>
    /// The factories are what the pages and the demos actually ask, so the default has to survive
    /// the trip through the endpoint types rather than merely being right on the options object.
    /// </summary>
    [Fact]
    public void Factories_Report_The_Emulator_When_Nothing_Is_Configured()
    {
        IOptions<FlociOptions> options = Options.Create(new FlociOptions());

        Assert.True(new S3ClientFactory(new AwsEndpoints(options)).UseEmulator);
        Assert.True(new BlobClientFactory(new AzureEndpoints(options)).UseEmulator);
        Assert.True(new StorageClientFactory(new GcpEndpoints(options)).UseEmulator);
    }

    /// <summary>
    /// Azure is the one provider whose real-cloud path needs a value rather than the absence of an
    /// override, because storage authenticates with an account key and there is no ambient chain to
    /// fall back on. Failing loudly at construction beats building a client that quietly addresses
    /// the emulator's well-known development account against a real endpoint.
    /// </summary>
    [Fact]
    public void Azure_Real_Cloud_Without_A_Connection_String_Throws_Rather_Than_Guessing()
    {
        IOptions<FlociOptions> options = Options.Create(new FlociOptions
        {
            Azure = new AzureEmulatorOptions { UseEmulator = false },
        });

        BlobClientFactory factory = new(new AzureEndpoints(options));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => factory.Create());

        Assert.Contains("ConnectionString", ex.Message, StringComparison.Ordinal);
    }
}
