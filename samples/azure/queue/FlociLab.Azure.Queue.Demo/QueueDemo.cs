using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using FlociLab.Core;

namespace FlociLab.Azure.Queue;

/// <summary>
/// Azure Queue Storage against floci-az. Ordinary Azure.Storage.Queues code — the only
/// emulator-aware line in the sample is in <see cref="QueueClientFactory"/>.
/// </summary>
public sealed class QueueDemo(QueueClientFactory factory) : IServiceDemo
{
    private const string MessageBody = "Hello from FlociLab.";

    public string Provider => CloudProvider.Azure;

    public string Slug => "queue";

    public string DisplayName => "Queue Storage";

    public string Category => "Messaging";

    public string Route => "/azure/queue";

    /// <summary>ListQueues, the direct analog of Blob's ListContainers: one request, no state.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            QueueServiceClient client = factory.Create();
            int count = 0;

            await foreach (Page<QueueItem> page in
                client.GetQueuesAsync(cancellationToken: ct).AsPages(pageSizeHint: 100).ConfigureAwait(false))
            {
                count = page.Values.Count;
                break;
            }

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListQueues returned {count} queue(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Building the client can itself fail — a misconfigured endpoint host is rejected while
        // the connection string is parsed, before any request goes out. That has to become a
        // failed step like any other: an iterator that throws on the first MoveNextAsync takes
        // down the circuit instead of rendering the reason. The exception is caught here and
        // yielded below, because C# forbids a yield inside a try that has a catch.
        QueueServiceClient? client = null;
        Exception? clientFailure = null;

        try
        {
            client = factory.Create();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clientFailure = ex;
        }

        if (client is null)
        {
            yield return DemoStep.Failed("QueueServiceClient", clientFailure!, "new QueueServiceClient(connectionString)");

            yield break;
        }

        // Unique per run, so two runs never collide and a leftover queue from a crashed run never
        // makes the next one fail. 24 chars, inside Azure's 3-63 lowercase-and-hyphens rule.
        string queueName = $"flocilab-queue-{Guid.NewGuid():N}"[..24];
        QueueClient queue = client.GetQueueClient(queueName);
        bool created = false;
        bool createConfirmed = false;
        string? messageId = null;
        string? popReceipt = null;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListQueues — before",
                $"GET {factory.ServiceUrl}?comp=list\nqueueService.GetQueuesAsync()",
                async () =>
                {
                    List<string> names = [];

                    await foreach (QueueItem item in client.GetQueuesAsync(cancellationToken: ct).ConfigureAwait(false))
                    {
                        names.Add($"  {item.Name}");
                    }

                    return $"{names.Count} queue(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateQueue",
                $"PUT {factory.ServiceUrl}/{queueName}\nqueue.CreateAsync()",
                async () =>
                {
                    // Set before the call, not after: if the PUT lands but the response does not
                    // come back, the queue exists and cleanup has to know about it. Cleanup treats
                    // an absent queue as a no-op, so claiming it early is free.
                    created = true;
                    Response response = await queue.CreateAsync(cancellationToken: ct).ConfigureAwait(false);

                    // Distinct from `created`: that one says "a PUT went out, so cleanup has to
                    // try", this one says "the queue demonstrably exists". Cleanup needs both —
                    // see DeleteQueueAsync for why a delete that removed nothing is not a success.
                    createConfirmed = true;

                    return $"HTTP {response.Status}\n"
                        + $"x-ms-request-id: {RequestIdOf(response)}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "SendMessage",
                $"POST {factory.ServiceUrl}/{queueName}/messages\nqueue.SendMessageAsync(\"{MessageBody}\")",
                async () =>
                {
                    Response<SendReceipt> response = await queue.SendMessageAsync(MessageBody, ct).ConfigureAwait(false);

                    return $"HTTP {response.GetRawResponse().Status} — MessageId: {response.Value.MessageId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ReceiveMessage",
                $"GET {factory.ServiceUrl}/{queueName}/messages\nqueue.ReceiveMessageAsync()",
                async () =>
                {
                    Response<QueueMessage> response = await queue.ReceiveMessageAsync(cancellationToken: ct).ConfigureAwait(false);
                    QueueMessage? message = response.Value;

                    // Both halves of the delete come from the *receive*, never from the send. Azure
                    // requires the pop receipt to belong to the message id it is paired with and
                    // answers 400 MessageNotFound otherwise; the send's id only happens to match
                    // while the queue holds exactly the one message this run put there.
                    messageId = message?.MessageId;
                    popReceipt = message?.PopReceipt;

                    // A round-trip that received nothing did not round-trip — an empty receive
                    // goes out red rather than claiming success for a message that never arrived.
                    if (message is null)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {response.GetRawResponse().Status} — no message; the message sent above did not arrive.");
                    }

                    return $"HTTP {response.GetRawResponse().Status} — Body: {message.MessageText}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteMessage",
                $"DELETE {factory.ServiceUrl}/{queueName}/messages/{{messageId}}\nqueue.DeleteMessageAsync()",
                async () =>
                {
                    if (messageId is null || popReceipt is null)
                    {
                        throw new InvalidOperationException("Skipped — ReceiveMessage returned no message to delete.");
                    }

                    Response response = await queue.DeleteMessageAsync(messageId, popReceipt, ct).ConfigureAwait(false);

                    return $"HTTP {response.Status}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created
                ? await DeleteQueueAsync(queue, factory.ServiceUrl, queueName, createConfirmed, ct).ConfigureAwait(false)
                : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// Azure reports both of the interesting failures inside a <see cref="RequestFailedException"/>,
    /// so <see cref="ProbeResult.FromException"/> — which inspects only the outermost exception —
    /// cannot classify them on its own. A 501 arrives as <see cref="RequestFailedException.Status"/>;
    /// a refused connection arrives as the same exception type with a status of 0 and a transport
    /// exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RequestFailedException { Status: (int)HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

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
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Queue Storage would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the queue.
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// Cleanup, and a step like any other: it goes green only when it actually removed the queue.
    /// <c>DeleteIfExists</c> answering false is never a success here, because this method is only
    /// reached after a create was attempted — so false means either the create never landed (the
    /// run is already broken and the page should not end on a green badge) or the queue this run
    /// made vanished under it. Both are findings; see docs/BLAZOR-PLAN.md §14 on cleanup steps that
    /// render green having achieved nothing.
    /// </summary>
    private static async Task<DemoStep> DeleteQueueAsync(QueueClient queue, string serviceUrl, string queueName, bool createConfirmed, CancellationToken ct)
    {
        string request = $"DELETE {serviceUrl}/{queueName}\nqueue.DeleteIfExistsAsync()";

        return await RunStepAsync("DeleteQueue — cleanup", request, async () =>
        {
            Response<bool> response = await queue.DeleteIfExistsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            int status = response.GetRawResponse().Status;

            if (!response.Value)
            {
                throw new InvalidOperationException(
                    $"HTTP {status} — nothing was removed: "
                    + (createConfirmed
                        ? $"'{queueName}' was created by this run but is already gone, so something else deleted it."
                        : $"'{queueName}' never existed, because CreateQueue above did not succeed."));
            }

            return $"HTTP {status} — removed the queue"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    private static string RequestIdOf(Response response)
        => response.Headers.TryGetValue("x-ms-request-id", out string? id) ? id : "(none)";

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
