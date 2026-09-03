using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.ServiceBus;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-az per class (docs/BLAZOR-PLAN.md §10), with its Service Bus Artemis sidecar
/// enabled and the Docker socket mounted so it can start one — Service Bus defaults to mocked mode
/// (management plane only) otherwise.
///
/// <para>
/// <b>The sidecar is a Docker-host singleton, and that shapes this whole fixture.</b> floci-az names
/// it <c>floci-az-servicebus-default</c> — derived from the namespace ("default"), with no per-run
/// or per-port suffix — and launches it as a *sibling* on the host daemon through the mounted
/// socket, not as a child Testcontainers can reap. So it outlives <c>this.flociAz</c>, and only one
/// can exist per machine no matter how many floci-az containers are alive. Two consequences, both
/// of which this fixture has to handle rather than assume away:
/// </para>
///
/// <para>
/// 1. <b>The AMQP port cannot simply be pinned.</b> <see cref="PreferredAmqpPort"/> is what this run
/// *asks* for, and it is deliberately not 5673 (the README/AppHost default) so a dev stack's own
/// sidecar is never mistaken for this run's. But if a sidecar of that fixed name is already up —
/// `dotnet run --project src/FlociLab.AppHost` in another terminal, both documented workflows in
/// CLAUDE.md — floci-az attaches to it and the asked-for port is simply not what is listening.
/// <see cref="InitializeAsync"/> therefore *discovers* the published port off the running container
/// instead of assuming it, so the tests work either way.
/// </para>
///
/// <para>
/// 2. <b>Only a sidecar this run created may be removed.</b> A blanket <c>docker rm -f</c> on that
/// fixed name would tear a developer's running dev stack apart from underneath it. Existence is
/// recorded before the sidecar is started and <see cref="DisposeAsync"/> removes it only if this run
/// is what brought it up.
/// </para>
///
/// <para>
/// The sidecar also starts <i>lazily</i>, on the first entity-management call rather than at boot —
/// as of floci-az 0.11.0 <c>START_ON_BOOT</c> is accepted but not honoured (§14). Since the demo's
/// clients run with <c>MaxRetries = 0</c>, a test that dialled AMQP before Artemis finished booting
/// would fail spuriously, so <see cref="InitializeAsync"/> forces the sidecar up and waits for the
/// port to accept a connection before any <c>[Fact]</c> runs.
/// </para>
///
/// <para>
/// <c>DeleteQueue — cleanup</c> is expected to fail every run: probing the running emulator shows
/// its router resolves the account and service type from the request path, and a GET/DELETE on the
/// bare queue name — the shape the official <see cref="ServiceBusAdministrationClient"/> always
/// sends, since real Service Bus has no account-in-path concept — is misread as a Blob request for
/// an account literally named after the queue, which 501s. Every test here pins that behaviour
/// rather than skipping it, so the suite becomes the tripwire for the day floci-az routes it
/// correctly.
/// </para>
/// </summary>
public sealed class AzureServiceBusTests : IAsyncLifetime
{
    private const int FlociAzPort = 4577;

    // What this run asks for when it is the one starting the sidecar. Deliberately not 5673 — see
    // this class's remarks.
    private const int PreferredAmqpPort = 5683;

    // Artemis's own container port. floci-az publishes it to a host port; which one is what
    // SidecarPublishedPortAsync reads back.
    private const string ArtemisAmqpContainerPort = "5672/tcp";

    // The sidecar's fixed name — see this class's remarks.
    private const string ArtemisSidecarName = "floci-az-servicebus-default";

    private static readonly TimeSpan AmqpStartupTimeout = TimeSpan.FromMinutes(2);

    // A plain ContainerBuilder rather than the FlociBuilder the S3 tests use — see AzureBlobTests
    // for why (Testcontainers.Floci hardcodes port 4566, floci-az listens on 4577).
    private readonly IContainer flociAz = new ContainerBuilder("floci/floci-az:latest")
        .WithPortBinding(FlociAzPort, assignRandomHostPort: true)
        .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock")
        .WithEnvironment("FLOCI_AZ_SERVICES_SERVICE_BUS_MOCKED", "false")
        .WithEnvironment("FLOCI_AZ_SERVICES_SERVICE_BUS_AMQP_PORT", PreferredAmqpPort.ToString(CultureInfo.InvariantCulture))
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci/health").ForPort(FlociAzPort)))
        .Build();

    private ServiceBusClientFactory factory = null!;

    private bool sidecarCreatedByThisRun;

    private string Endpoint => $"http://{this.flociAz.Hostname}:{this.flociAz.GetMappedPublicPort(FlociAzPort)}";

    public async ValueTask InitializeAsync()
    {
        await this.flociAz.StartAsync(TestContext.Current.CancellationToken);

        // Recorded *before* anything starts the sidecar, so DisposeAsync can tell "we brought this
        // up" from "it was someone else's and must be left alone".
        bool sidecarExistedAlready = await SidecarPublishedPortAsync() is not null;

        // Force the lazily-started sidecar up. Creating a queue is the cheapest call that does it;
        // listing queues answers 200 without ever starting Artemis (verified by curl against a
        // running floci-az 0.11.0, 2026-09-03). The queue is left behind because floci-az cannot
        // delete one — harmless in a throwaway container.
        ServiceBusAdministrationClient warmup =
            new ServiceBusClientFactory(EndpointsFor(this.Endpoint, PreferredAmqpPort)).CreateAdministrationClient();

        await warmup.CreateQueueAsync($"flocilab-warmup-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        this.sidecarCreatedByThisRun = !sidecarExistedAlready;

        // Discovered rather than assumed: if the sidecar was already running, it is published on
        // whichever port *that* stack asked for, not PreferredAmqpPort.
        int amqpPort = await SidecarPublishedPortAsync() ?? PreferredAmqpPort;

        this.factory = new ServiceBusClientFactory(EndpointsFor(this.Endpoint, amqpPort));

        await WaitForAmqpAsync(amqpPort, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await this.flociAz.DisposeAsync();

        // floci-az does not stop this sidecar when it stops itself, so it has to go explicitly —
        // but only when this run is what started it. Removing a sidecar that was already up would
        // take down a concurrently running dev stack's Service Bus (see this class's remarks).
        if (this.sidecarCreatedByThisRun)
        {
            await RunDockerAsync($"rm -f {ArtemisSidecarName}");
        }
    }

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new ServiceBusDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed. The management plane is HTTP, so this
    /// never touches the AMQP port discovered above.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        ServiceBusDemo demo = new(new ServiceBusClientFactory(EndpointsFor("http://127.0.0.1:1", PreferredAmqpPort)));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// Every step but cleanup genuinely round-trips through the Artemis-backed AMQP data plane —
    /// unlike Queue Storage's sample, this is not a fully broken service. DeleteQueue is the one
    /// documented gap (see this class's remarks); it is still attempted and still yielded, just red.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds_Except_The_Documented_Cleanup_Gap()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new ServiceBusDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.True(s.Succeeded, $"ListQueues — before: {s.Error}"),
            s => Assert.True(s.Succeeded, $"CreateQueue: {s.Error}"),
            s => Assert.True(s.Succeeded, $"SendMessage: {s.Error}"),
            s => Assert.True(s.Succeeded, $"ReceiveMessage: {s.Error}"),
            s => Assert.True(s.Succeeded, $"CompleteMessage: {s.Error}"),
            s => Assert.False(
                s.Succeeded,
                "DeleteQueue — cleanup succeeded; floci-az may have fixed the routing gap — update this test and docs/BLAZOR-PLAN.md §14."));
    }

    /// <summary>
    /// The gap in isolation, pinned directly rather than only visible inside a failing round trip:
    /// a clean 501, not a 404 or a client-side deserialization throw — an honest read of what
    /// floci-az actually said. Verified against floci-az 0.11.0, 2026-09-03.
    /// </summary>
    [Fact]
    public async Task DeleteQueue_Is_Misrouted_To_Blob_And_Answers_NotImplemented()
    {
        ServiceBusAdministrationClient admin = this.factory.CreateAdministrationClient();
        string queueName = $"flocilab-probe-{Guid.NewGuid():N}";

        await admin.CreateQueueAsync(queueName, TestContext.Current.CancellationToken);

        ServiceBusException ex = await Assert.ThrowsAsync<ServiceBusException>(
            async () => await admin.DeleteQueueAsync(queueName, TestContext.Current.CancellationToken));

        RequestFailedException inner = Assert.IsType<RequestFailedException>(ex.InnerException);
        Assert.Equal(501, inner.Status);
    }

    /// <summary>
    /// The host port the running Artemis sidecar publishes its AMQP listener on, or <c>null</c> if
    /// no sidecar is running. Doubles as the existence check in <see cref="InitializeAsync"/>.
    /// </summary>
    private static async Task<int?> SidecarPublishedPortAsync()
    {
        (int exitCode, string output) = await RunDockerAsync(
            $"inspect {ArtemisSidecarName} --format \"{{{{(index (index .NetworkSettings.Ports \\\"{ArtemisAmqpContainerPort}\\\") 0).HostPort}}}}\"");

        if (exitCode != 0)
        {
            return null;
        }

        return int.TryParse(output.Trim(), CultureInfo.InvariantCulture, out int port) ? port : null;
    }

    /// <summary>
    /// Waits for Artemis to accept a TCP connection. Booting the <c>apache/activemq-artemis</c>
    /// image takes seconds — longer on the first run, which pulls it — and the demo's clients run
    /// with <c>MaxRetries = 0</c>, so without this the first AMQP test to run would fail
    /// spuriously depending on which <c>[Fact]</c> xUnit happened to schedule first.
    /// </summary>
    private static async Task WaitForAmqpAsync(int port, CancellationToken ct)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(AmqpStartupTimeout.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            try
            {
                using TcpClient probe = new();
                await probe.ConnectAsync("127.0.0.1", port, ct);

                return;
            }
            // Nothing listening yet. Retried until the deadline, then reported as a failure naming
            // the port, which is far easier to read than an AMQP timeout deep inside a demo step.
            catch (SocketException) when (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    $"The Artemis sidecar '{ArtemisSidecarName}' never accepted a connection on port {port} "
                    + $"within {AmqpStartupTimeout.TotalSeconds:0}s. Check `docker logs {ArtemisSidecarName}`.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Runs the Docker CLI. Used rather than Testcontainers' own client because this sibling
    /// container was launched by floci-az through the mounted socket, so Testcontainers never had a
    /// handle on it to reap. It therefore resolves the daemon the way every other Docker call in
    /// this repo's tooling does — through the ambient CLI context.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunDockerAsync(string arguments)
    {
        ProcessStartInfo startInfo = new("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using Process? process = Process.Start(startInfo);

            if (process is null)
            {
                return (-1, string.Empty);
            }

            // Read before waiting: a process whose output fills the pipe buffer blocks forever on
            // exit if nobody is draining it.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        // No Docker CLI on PATH. Every caller here treats that as "no sidecar to see or remove",
        // which degrades to the previous pinned-port behaviour rather than failing the class in
        // its constructor-equivalent.
        catch (Win32Exception)
        {
            return (-1, string.Empty);
        }
    }

    private static AzureEndpoints EndpointsFor(string endpoint, int amqpPort) => new(Options.Create(new FlociOptions
    {
        Azure = new AzureEmulatorOptions { Endpoint = endpoint, ServiceBusAmqpPort = amqpPort },
    }));
}
