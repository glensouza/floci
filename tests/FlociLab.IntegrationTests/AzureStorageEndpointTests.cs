using Azure.Storage.Blobs;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// No emulator needed — these pin an Azure.Storage parsing rule that is easy to regress and
/// expensive to debug, because breaking it produces a container create that returns 201 followed
/// by an upload that 404s (plan §14).
///
/// <para>
/// Azure.Storage reads the account out of the URL path only when the host is a literal IPv4
/// address. Against a DNS name it assumes the production shape — account in the subdomain, first
/// path segment is the container — so <c>http://localhost:4577/devstoreaccount1</c> parses with
/// the account name sitting in the container slot. <see cref="AzureEndpoints.StorageRoot"/>
/// rewrites the host to keep the SDK on the emulator path.
/// </para>
/// </summary>
public sealed class AzureStorageEndpointTests
{
    [Theory]
    [InlineData("http://localhost:4577", "http://127.0.0.1:4577")]
    [InlineData("http://127.0.0.1:4577", "http://127.0.0.1:4577")]
    [InlineData("http://localhost:32768", "http://127.0.0.1:32768")]
    // Uri.Host hands back an IPv6 literal already bracketed and IPAddress.TryParse accepts that
    // form, so "::1" is read as a literal, not as a loopback name. It still has to end up on the
    // IPv4 spelling of the same machine rather than "[[::1]]", which no client can be built from.
    [InlineData("http://[::1]:4577", "http://127.0.0.1:4577")]
    public void StorageRoot_Rewrites_Loopback_Names_To_An_Address(string endpoint, string expected)
        => Assert.Equal(expected, EndpointsFor(endpoint).StorageRoot);

    /// <summary>
    /// 127.0.0.2 is loopback as far as <see cref="Uri.IsLoopback"/> is concerned, so a rewrite
    /// that tested loopback before it tested for a literal would move the endpoint to a different
    /// address than the one configured. It is already IPv4; it has to be left alone.
    /// </summary>
    [Fact]
    public void StorageRoot_Leaves_A_Non_Standard_Loopback_Literal_Alone()
        => Assert.Equal("http://127.0.0.2:4577", EndpointsFor("http://127.0.0.2:4577").StorageRoot);

    /// <summary>
    /// A non-loopback IPv6 literal is the one shape that must not be handed back: the connection
    /// would succeed and the SDK would still read the account as the container, so the round-trip
    /// fails as a create-201-then-upload-404 that reads like an emulator bug. Fail naming the
    /// constraint instead.
    /// </summary>
    [Fact]
    public void StorageRoot_Refuses_A_Host_That_Cannot_Carry_The_Account_In_The_Path()
    {
        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => EndpointsFor("http://[2001:db8::1]:4577").StorageRoot);

        Assert.Contains("IPv4", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name that does not resolve is handed back unchanged, so the storage call fails at the
    /// transport and classifies as Unreachable rather than as a broken sample — and it is not
    /// cached, because the usual cause is an emulator container that has not started yet.
    /// </summary>
    [Fact]
    public void StorageRoot_Does_Not_Cache_A_Name_That_Failed_To_Resolve()
    {
        AzureEndpoints endpoints = EndpointsFor("http://floci-az-does-not-exist.invalid:4577");

        Assert.Equal("http://floci-az-does-not-exist.invalid:4577", endpoints.StorageRoot);
        Assert.Equal("http://floci-az-does-not-exist.invalid:4577", endpoints.StorageRoot);
    }

    [Fact]
    public void StorageConnectionString_Points_Every_Service_At_The_Account_Path()
    {
        string connectionString = EndpointsFor("http://localhost:4577").StorageConnectionString();

        Assert.Contains("BlobEndpoint=http://127.0.0.1:4577/devstoreaccount1;", connectionString, StringComparison.Ordinal);
        Assert.Contains("QueueEndpoint=http://127.0.0.1:4577/devstoreaccount1;", connectionString, StringComparison.Ordinal);
        Assert.Contains("TableEndpoint=http://127.0.0.1:4577/devstoreaccount1;", connectionString, StringComparison.Ordinal);
    }

    /// <summary>
    /// The assertion that actually matters: not the shape of our string, but what the SDK makes of
    /// it. A blob built from the connection string has to know it is in the container we asked for.
    /// </summary>
    [Fact]
    public void Sdk_Resolves_The_Account_Container_And_Blob_From_The_Connection_String()
    {
        BlobServiceClient service = new(EndpointsFor("http://localhost:4577").StorageConnectionString());
        BlobClient blob = service.GetBlobContainerClient("demo-container").GetBlobClient("hello/floci.txt");

        Assert.Equal("devstoreaccount1", blob.AccountName);
        Assert.Equal("demo-container", blob.BlobContainerName);
        Assert.Equal("hello/floci.txt", blob.Name);
        Assert.Equal("http://127.0.0.1:4577/devstoreaccount1/demo-container/hello/floci.txt", blob.Uri.ToString());
    }

    /// <summary>
    /// What the fix is defending against, stated as a fact about the SDK rather than about us. If
    /// this ever starts failing, Azure.Storage has learned to honour a path-style account on a DNS
    /// host and <see cref="AzureEndpoints.StorageRoot"/> can go.
    /// </summary>
    [Fact]
    public void Sdk_Misreads_The_Account_As_A_Container_On_A_Dns_Host()
    {
        const string ConnectionString =
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
            "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
            "BlobEndpoint=http://localhost:4577/devstoreaccount1;";

        BlobServiceClient service = new(ConnectionString);
        BlobClient blob = service.GetBlobContainerClient("demo-container").GetBlobClient("hello/floci.txt");

        Assert.Equal("devstoreaccount1", blob.BlobContainerName);
        Assert.Equal("http://localhost:4577/devstoreaccount1/hello/floci.txt", blob.Uri.ToString());
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
