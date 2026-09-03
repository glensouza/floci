using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FlociLab.Core;

namespace FlociLab.Azure.ServiceBus;

/// <summary>
/// Azure Service Bus against floci-az. Ordinary Azure.Messaging.ServiceBus code — the only
/// emulator-aware lines are in <see cref="ServiceBusClientFactory"/>.
///
/// <para>
/// <c>DeleteQueue — cleanup</c> is expected to fail against floci-az every run: probing the running
/// emulator shows its router resolves the account and service type from a request's path, and a
/// GET/DELETE on a bare <c>/{queueName}</c> — the shape the official
/// <see cref="ServiceBusAdministrationClient"/> always sends, since real Service Bus has no
/// account-in-path concept — is misread as a Blob request for an account literally named after the
/// queue, which 501s. <c>CreateQueueAsync</c> (PUT) is not affected: the router falls back to
/// Service Bus by content-type when the path does not resolve, but that fallback does not extend
/// to GET/DELETE. Confirmed against floci-az 0.11.0, 2026-09-03: <c>GET
/// /{account}-servicebus/{queue}</c> (the account-prefixed path the SDK never sends) answers 200;
/// the bare path answers a clean 501. See docs/BLAZOR-PLAN.md §14.
/// </para>
/// </summary>
public sealed class ServiceBusDemo(ServiceBusClientFactory factory) : IServiceDemo
{
    private const string MessageBody = "Hello from FlociLab.";

    // floci-az cannot delete a queue (see this class's remarks), so every run of this demo leaks
    // one into the emulator's persistent volume — and the coverage page probes every registered
    // demo on load. Without a bound, that probe pages through every queue the machine has ever
    // created, and gets slower for the lifetime of the volume.
    private const int MaxQueuesListed = 100;

    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    public string Provider => CloudProvider.Azure;

    public string Slug => "servicebus";

    public string DisplayName => "Service Bus";

    public string Category => "Messaging";

    public string Route => "/azure/servicebus";

    /// <summary>Lists queues over the management plane — cheap, stateless, and (unlike a single-entity GET) not affected by the routing gap documented on this class.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            ServiceBusAdministrationClient admin = factory.CreateAdministrationClient();
            string summary = await CountQueuesAsync(admin, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"Listed {summary} over the management plane.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Building the clients can itself fail — an iterator that throws on the first
        // MoveNextAsync takes down the circuit instead of rendering the reason. Caught here and
        // yielded below, because C# forbids a yield inside a try that has a catch.
        ServiceBusAdministrationClient? admin = null;
        ServiceBusClient? client = null;
        Exception? clientFailure = null;

        try
        {
            admin = factory.CreateAdministrationClient();
            client = factory.CreateClient();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clientFailure = ex;
        }

        if (admin is null || client is null)
        {
            yield return DemoStep.Failed(
                "ServiceBusClient", clientFailure!, "new ServiceBusAdministrationClient(...) / new ServiceBusClient(...)");

            yield break;
        }

        // Unique per run, so two runs never collide and a leftover queue from a crashed run never
        // makes the next one fail. Service Bus allows up to 260 characters, far looser than Storage
        // Queue's 3-63 lowercase-and-hyphens rule, so the full GUID needs no cropping.
        string queueName = $"flocilab-servicebus-{Guid.NewGuid():N}";
        bool created = false;
        ServiceBusSender? sender = null;
        ServiceBusReceiver? receiver = null;
        ServiceBusReceivedMessage? received = null;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListQueues — before",
                $"GET {factory.ManagementUrl}/$Resources/queues\nadministrationClient.GetQueuesAsync()",
                () => CountQueuesAsync(admin, ct)).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateQueue",
                $"PUT {factory.ManagementUrl}/{queueName}\nadministrationClient.CreateQueueAsync(\"{queueName}\")",
                async () =>
                {
                    // Set before the call, not after: if the PUT lands but the response does not
                    // come back, the queue exists and cleanup has to know about it.
                    created = true;
                    QueueProperties queue = await admin.CreateQueueAsync(queueName, ct).ConfigureAwait(false);

                    return $"Created — Status: {queue.Status}";
                }).ConfigureAwait(false);

            // Guarded for the same reason client construction above is: these are ordinary calls
            // that can throw (a disposed client, a rejected entity path), and an unguarded throw
            // here escapes the iterator into the circuit instead of rendering as a failed step —
            // discarding the cleanup step the finally has already computed on the way out.
            Exception? linkFailure = null;

            try
            {
                sender = client.CreateSender(queueName);
                receiver = client.CreateReceiver(queueName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                linkFailure = ex;
            }

            if (sender is null || receiver is null)
            {
                yield return DemoStep.Failed(
                    "ServiceBusSender / ServiceBusReceiver",
                    linkFailure!,
                    $"client.CreateSender(\"{queueName}\") / client.CreateReceiver(\"{queueName}\")");
            }
            else
            {
                // Non-nullable copies, so the lambdas below capture a value the compiler already
                // knows is non-null rather than each needing a null-forgiving operator.
                ServiceBusSender messageSender = sender;
                ServiceBusReceiver messageReceiver = receiver;

                yield return await RunStepAsync(
                    "SendMessage",
                    $"AMQP {factory.AmqpEndpoint}\nsender.SendMessageAsync(\"{MessageBody}\")",
                    async () =>
                    {
                        await messageSender.SendMessageAsync(new ServiceBusMessage(MessageBody), ct).ConfigureAwait(false);

                        return "Sent.";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "ReceiveMessage",
                    $"AMQP {factory.AmqpEndpoint}\nreceiver.ReceiveMessageAsync()",
                    async () =>
                    {
                        received = await messageReceiver.ReceiveMessageAsync(ReceiveTimeout, ct).ConfigureAwait(false);

                        // A round-trip that received nothing did not round-trip — an empty receive
                        // goes out red rather than claiming success for a message that never arrived.
                        if (received is null)
                        {
                            throw new InvalidOperationException("No message; the message sent above did not arrive.");
                        }

                        return $"Body: {received.Body} — DeliveryCount: {received.DeliveryCount}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CompleteMessage",
                    $"AMQP {factory.AmqpEndpoint}\nreceiver.CompleteMessageAsync()",
                    async () =>
                    {
                        if (received is null)
                        {
                            throw new InvalidOperationException("Skipped — ReceiveMessage returned no message to complete.");
                        }

                        // Peek-lock is the SDK default: the message is only hidden, not removed, until
                        // this call. Without it the message reappears once its lock expires.
                        await messageReceiver.CompleteMessageAsync(received, ct).ConfigureAwait(false);

                        return "Completed — the message is permanently removed.";
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean namespace. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            //
            // Each disposal is its own try/catch, not one around the whole block: DisposeAsync on a
            // link that opened and then faulted calls the SDK's CloseAsync, which rethrows — and an
            // unwrapped throw here would skip DeleteQueueAsync and client.DisposeAsync below,
            // leaking both the queue and the connection on exactly the runs where cleanup matters
            // most. The failure is not this run's business once the link is going away regardless.
            //
            // Caught broadly rather than as ServiceBusException alone: a close that times out
            // surfaces as TaskCanceledException or TimeoutException, and an already-disposed client
            // as ObjectDisposedException. Narrowing to one type would let exactly the cases this
            // block exists to contain escape and skip the cleanup below.
            if (receiver is not null)
            {
                try
                {
                    await receiver.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }
            }

            if (sender is not null)
            {
                try
                {
                    await sender.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }
            }

            cleanup = created
                ? await DeleteQueueAsync(admin, factory.ManagementUrl, queueName).ConfigureAwait(false)
                : null;

            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// Unwraps both planes' exception shapes: the management plane's <see cref="RequestFailedException"/>
    /// (a 501 for "not implemented", a transport failure for "unreachable"), and the AMQP data
    /// plane's <see cref="ServiceBusException"/>, which carries its own typed
    /// <see cref="ServiceBusFailureReason"/> rather than an HTTP status.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RequestFailedException { Status: (int)HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                // The AMQP transport failing to open (sidecar not yet started, or the emulator down)
                // reports as ServiceCommunicationProblem rather than a bare socket exception, since
                // Azure.Messaging.ServiceBus wraps its own transport in a ServiceBusException before
                // this method ever sees it. ServiceTimeout is deliberately excluded: it is also what
                // a live namespace returns when an operation just ran past TryTimeout (realistic here
                // — MaxRetries is 0 and the Artemis sidecar starts lazily on the first management
                // call), so treating it as Unreachable would report a working emulator as absent.
                case ServiceBusException { Reason: ServiceBusFailureReason.ServiceCommunicationProblem }:
                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case RequestFailedException { Status: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Counts queues, stopping at <see cref="MaxQueuesListed"/> — see that constant for why the
    /// bound is load-bearing rather than tidiness. Shared by the probe and the ListQueues step so
    /// the two can never report a different number for the same namespace.
    /// </summary>
    private static async Task<string> CountQueuesAsync(ServiceBusAdministrationClient admin, CancellationToken ct)
    {
        int count = 0;

        await foreach (QueueProperties _ in admin.GetQueuesAsync(ct).ConfigureAwait(false))
        {
            // Breaking stops the SDK requesting the next page, which is the point of the cap.
            if (++count == MaxQueuesListed)
            {
                return $"{MaxQueuesListed}+ queue(s)";
            }
        }

        return $"{count} queue(s)";
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Service Bus would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the queue.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// Cleanup, and a step like any other — expected to fail every run against floci-az; see this
    /// class's remarks for why. It is still attempted rather than skipped: the day floci-az routes
    /// an unprefixed DELETE correctly, this step turns green and stops leaving queues behind.
    /// </summary>
    private static async Task<DemoStep> DeleteQueueAsync(ServiceBusAdministrationClient admin, string managementUrl, string queueName)
    {
        string request = $"DELETE {managementUrl}/{queueName}\nadministrationClient.DeleteQueueAsync(\"{queueName}\")";

        return await RunStepAsync("DeleteQueue — cleanup", request, async () =>
        {
            await admin.DeleteQueueAsync(queueName, CancellationToken.None).ConfigureAwait(false);

            return "Deleted the queue.";
        }).ConfigureAwait(false);
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
