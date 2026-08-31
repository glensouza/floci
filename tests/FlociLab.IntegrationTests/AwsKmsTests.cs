using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using FlociLab.Aws.Kms;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Testcontainers.Floci;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator the
/// AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class AwsKmsTests : IAsyncLifetime
{
    // Same reasoning as AwsS3Tests: pinned to :latest so the tripwire tracks the same build the
    // AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private KmsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new KmsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new KmsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new KmsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListKeys — before", s.Title),
            s => Assert.Equal("CreateKey", s.Title),
            s => Assert.Equal("Encrypt", s.Title),
            s => Assert.Equal("Decrypt", s.Title),
            s => Assert.Equal("ScheduleKeyDeletion — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "Decrypt").Response);
    }

    /// <summary>
    /// Real KMS has no immediate delete: <c>ScheduleKeyDeletion</c> moves a key to
    /// <c>PendingDeletion</c> for its window rather than removing it, so — unlike every other
    /// Phase 2 sample — a run does not return the account to its pre-run state. This is the
    /// tripwire for that: if it ever starts failing, floci has started hard-deleting keys on
    /// schedule, which is the signal to revisit the cleanup step's comments in KmsDemo.
    /// </summary>
    [Fact]
    public async Task Cleanup_Schedules_Deletion_Rather_Than_Removing_The_Key()
    {
        KmsKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        string keyId = await keyManagement.CreateKeyAsync($"flocilab-cap-{Guid.NewGuid():N}", ct);
        await keyManagement.DeleteKeyAsync(keyId, ct);

        // Still listed — a hard delete would remove it, the way DeleteTable/DeleteQueue do for
        // their own capabilities.
        Assert.Contains(keyId, (await keyManagement.ListKeysAsync(ct)).Select(k => k.Id));

        using IAmazonKeyManagementService client = this.factory.Create();
        DescribeKeyResponse described = await client.DescribeKeyAsync(new DescribeKeyRequest { KeyId = keyId }, ct);

        Assert.Equal(KeyState.PendingDeletion, described.KeyMetadata.KeyState);
    }

    /// <summary>
    /// floci 1.7.0 does not encrypt (plan §14). CiphertextBlob is the ASCII envelope
    /// <c>kms:v2:&lt;KeyId&gt;:&lt;16 hex&gt;::&lt;base64 plaintext&gt;</c> — the plaintext comes
    /// back out with two base64 decodes and no key. Asserted rather than skipped, the same way a
    /// 501 is: this is the tripwire that says when floci implements real encryption, at which
    /// point KmsDemo's warning line, plan §14 and the episode's Gotchas beat all need revisiting.
    /// </summary>
    [Fact]
    public async Task Encrypt_Does_Not_Actually_Encrypt_On_Floci()
    {
        KmsKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] plaintext = "recoverable without the key"u8.ToArray();

        string keyId = await keyManagement.CreateKeyAsync($"flocilab-cipher-{Guid.NewGuid():N}", ct);
        byte[] ciphertext = await keyManagement.EncryptAsync(keyId, plaintext, ct);

        // Not byte-identical to the plaintext — the envelope around it is real, which is exactly
        // why the demo page's cheap "did the bytes change?" guard cannot catch this on its own.
        Assert.NotEqual(plaintext, ciphertext);

        string envelope = Encoding.UTF8.GetString(ciphertext);

        Assert.StartsWith("kms:v2:", envelope);
        Assert.Contains(Convert.ToBase64String(plaintext), envelope);
    }

    /// <summary>
    /// The half floci does model faithfully: a ciphertext is bound to the key that produced it, so
    /// decrypting under a different key fails the way real KMS fails. Worth pinning next to the
    /// test above — it is the reason the sample is still a genuine test of the SDK wiring.
    /// </summary>
    [Fact]
    public async Task Decrypt_Under_The_Wrong_Key_Fails()
    {
        KmsKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        string keyId = await keyManagement.CreateKeyAsync($"flocilab-a-{Guid.NewGuid():N}", ct);
        string otherKeyId = await keyManagement.CreateKeyAsync($"flocilab-b-{Guid.NewGuid():N}", ct);
        byte[] ciphertext = await keyManagement.EncryptAsync(keyId, "bound to one key"u8.ToArray(), ct);

        await Assert.ThrowsAsync<IncorrectKeyException>(
            () => keyManagement.DecryptAsync(otherKeyId, ciphertext, ct));
    }

    /// <summary>The capability the key-management comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task KeyManagement_Capability_RoundTrips()
    {
        KmsKeyManagement keyManagement = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] plaintext = "capability round-trip"u8.ToArray();

        string keyId = await keyManagement.CreateKeyAsync($"flocilab-cap-{Guid.NewGuid():N}", ct);

        Assert.Contains(keyId, (await keyManagement.ListKeysAsync(ct)).Select(k => k.Id));

        byte[] ciphertext = await keyManagement.EncryptAsync(keyId, plaintext, ct);
        byte[] decrypted = await keyManagement.DecryptAsync(keyId, ciphertext, ct);

        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        KmsDemo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);
            }
        });

        Assert.DoesNotContain(steps, s => !s.Succeeded);
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        KmsDemo demo = new(new KmsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
