using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using FlociLab.Core;

namespace FlociLab.Aws.Sqs;

/// <summary>
/// Amazon SQS against floci. Ordinary AWSSDK.SQS code — the only emulator-aware line in the sample
/// is in <see cref="SqsClientFactory"/>.
/// </summary>
public sealed class SqsDemo(SqsClientFactory factory) : IServiceDemo
{
    private const string MessageBody = "Hello from FlociLab.";

    public string Provider => CloudProvider.Aws;

    public string Slug => "sqs";

    public string DisplayName => "SQS";

    public string Category => "Messaging";

    public string Route => "/aws/sqs";

    /// <summary>ListQueues — one request, no state, and the cheapest call SQS has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonSQS client = factory.Create();
            ListQueuesResponse response = await client.ListQueuesAsync(new ListQueuesRequest(), ct).ConfigureAwait(false);
            int count = response.QueueUrls?.Count ?? 0;

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
        using IAmazonSQS client = factory.Create();

        // Unique per run, so two runs never collide and a leftover queue from a crashed run never
        // makes the next one fail. SQS allows up to 80 chars of alphanumerics/hyphens/underscores.
        string queueName = $"flocilab-sqs-{Guid.NewGuid():N}";
        bool created = false;
        string? queueUrl = null;
        string? receiptHandle = null;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListQueues — before",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSQS.ListQueues\nsqs.ListQueuesAsync(new ListQueuesRequest())",
                async () =>
                {
                    ListQueuesResponse response = await client.ListQueuesAsync(new ListQueuesRequest(), ct).ConfigureAwait(false);
                    IEnumerable<string> urls = response.QueueUrls?.Select(u => $"  {u}") ?? [];

                    return $"HTTP {(int)response.HttpStatusCode} — {response.QueueUrls?.Count ?? 0} queue(s)\n"
                        + string.Join('\n', urls);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateQueue",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSQS.CreateQueue\nsqs.CreateQueueAsync(new CreateQueueRequest {{ QueueName = \"{queueName}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the queue exists and cleanup has to know about it. Cleanup
                    // treats an absent queue as a no-op, so claiming it early is free.
                    created = true;
                    CreateQueueResponse response = await client.CreateQueueAsync(
                        new CreateQueueRequest { QueueName = queueName }, ct).ConfigureAwait(false);
                    queueUrl = response.QueueUrl;

                    return $"HTTP {(int)response.HttpStatusCode} — QueueUrl: {response.QueueUrl}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "SendMessage",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSQS.SendMessage\nsqs.SendMessageAsync(new SendMessageRequest {{ QueueUrl = \"{queueUrl}\", MessageBody = \"{MessageBody}\" }})",
                async () =>
                {
                    // CreateQueue's own step already reports why. This one still has to go out
                    // red: nothing was sent, and a green badge here would claim otherwise.
                    if (queueUrl is null)
                    {
                        throw new InvalidOperationException("Skipped — CreateQueue did not return a queue URL.");
                    }

                    SendMessageResponse response = await client.SendMessageAsync(
                        new SendMessageRequest { QueueUrl = queueUrl, MessageBody = MessageBody }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — MessageId: {response.MessageId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ReceiveMessage",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSQS.ReceiveMessage\nsqs.ReceiveMessageAsync(new ReceiveMessageRequest {{ QueueUrl = \"{queueUrl}\", MaxNumberOfMessages = 1, WaitTimeSeconds = 2 }})",
                async () =>
                {
                    if (queueUrl is null)
                    {
                        throw new InvalidOperationException("Skipped — CreateQueue did not return a queue URL.");
                    }

                    ReceiveMessageResponse response = await client.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = queueUrl,
                            MaxNumberOfMessages = 1,
                            // Short of this, the default is a 0s short-poll; a couple of seconds of
                            // long-poll absorbs propagation delay instead of racing the SendMessage
                            // that just happened, without slowing a healthy run down noticeably.
                            WaitTimeSeconds = 2,
                        }, ct).ConfigureAwait(false);
                    Message? message = response.Messages?.FirstOrDefault();
                    receiptHandle = message?.ReceiptHandle;

                    // A round-trip that received nothing did not round-trip. The lede promises
                    // this page shows what floci actually answered, so an empty receive goes out
                    // red — six green steps for a run that delivered no message is the page lying.
                    if (message is null)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — 0 message(s); the message sent above did not arrive within the long poll.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — Body: {message.Body}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteMessage",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSQS.DeleteMessage\nsqs.DeleteMessageAsync(new DeleteMessageRequest {{ QueueUrl = \"{queueUrl}\", ReceiptHandle = ... }})",
                async () =>
                {
                    if (queueUrl is null || receiptHandle is null)
                    {
                        throw new InvalidOperationException("Skipped — ReceiveMessage returned no message to delete.");
                    }

                    DeleteMessageResponse response = await client.DeleteMessageAsync(
                        new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = receiptHandle }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await this.DeleteQueueAsync(client, queueName, ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// The AWS SDK reports both of the interesting failures inside an
    /// <see cref="AmazonServiceException"/>, so <see cref="ProbeResult.FromException"/> — which
    /// inspects only the outermost exception — cannot classify them on its own. A 501 arrives as
    /// a status code on the exception; a refused connection arrives with no status code at all
    /// and a transport exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real SQS would not.
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

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Resolves the queue's URL by name rather than reusing whatever <see cref="RunAsync"/>
    /// captured from CreateQueue's response: if that response never arrived (a dropped connection
    /// after the request landed), the queue still exists server-side and the name is all cleanup
    /// has. A queue that genuinely never got created answers the lookup with
    /// <see cref="QueueDoesNotExistException"/>, which is a clean run finishing, not a cleanup
    /// failure worth showing in red. The calls use <see cref="CancellationToken.None"/> — a run
    /// that was cancelled still has a queue to remove.
    /// </summary>
    private async Task<DemoStep> DeleteQueueAsync(IAmazonSQS client, string queueName, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSQS.GetQueueUrl, AmazonSQS.DeleteQueue\nsqs.DeleteQueueAsync(sqs.GetQueueUrlAsync(\"{queueName}\").QueueUrl)";

        return await RunStepAsync("DeleteQueue — cleanup", request, async () =>
        {
            string resolvedUrl;

            try
            {
                GetQueueUrlResponse lookup = await client.GetQueueUrlAsync(
                    new GetQueueUrlRequest { QueueName = queueName }, CancellationToken.None).ConfigureAwait(false);
                resolvedUrl = lookup.QueueUrl;
            }
            catch (QueueDoesNotExistException)
            {
                return "Nothing to remove — the queue was never created.";
            }

            DeleteQueueResponse response = await client.DeleteQueueAsync(resolvedUrl, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — removed the queue"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
