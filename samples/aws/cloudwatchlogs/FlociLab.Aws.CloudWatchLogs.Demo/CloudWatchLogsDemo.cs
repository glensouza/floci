using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using FlociLab.Core;
using InvalidOperationException = System.InvalidOperationException;

namespace FlociLab.Aws.CloudWatchLogs;

/// <summary>
/// Amazon CloudWatch Logs against floci. Ordinary AWSSDK.CloudWatchLogs code — the only
/// emulator-aware line in the sample is in <see cref="CloudWatchLogsClientFactory"/>.
/// </summary>
public sealed class CloudWatchLogsDemo(CloudWatchLogsClientFactory factory) : IServiceDemo
{
    // Real CloudWatch Logs ingestion is asynchronous; floci's is not, so against the emulator the
    // first read always hits and these never cost anything.
    private static readonly TimeSpan ReadBackPollDelay = TimeSpan.FromMilliseconds(500);

    private const int ReadBackPollAttempts = 20;

    public string Provider => CloudProvider.Aws;

    public string Slug => "cloudwatchlogs";

    public string DisplayName => "CloudWatch Logs";

    public string Category => "Observability";

    public string Route => "/aws/cloudwatchlogs";

    /// <summary>DescribeLogGroups — one request, no state, and the cheapest call the service has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonCloudWatchLogs client = factory.Create();
            DescribeLogGroupsResponse response = await client.DescribeLogGroupsAsync(
                new DescribeLogGroupsRequest(), ct).ConfigureAwait(false);

            // AWSSDK v4 leaves an absent response collection null rather than empty (§14), and an
            // account with no log groups is the normal state of a fresh emulator — so an unguarded
            // .Count would throw here and Classify would report a healthy service as Error.
            int count = response.LogGroups?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"DescribeLogGroups returned {count} group(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonCloudWatchLogs client = factory.Create();

        // Unique per run, so two runs never collide.
        string suffix = Guid.NewGuid().ToString("N");
        string logGroupName = $"/flocilab/cloudwatchlogs-{suffix}";
        string logStreamName = $"flocilab-stream-{suffix}";
        string message = $"Hello from FlociLab, run {suffix}.";

        // Declared out here rather than inside the try so the cleanup in the finally can read it.
        // A cleanup that re-derives "did this land?" from a listing gets the answer wrong whenever
        // the listing is stale, so gate cleanup on "the request was issued" instead.
        bool logGroupAttempted = false;
        DemoStep? deleteLogGroupStep = null;

        try
        {
            // A do/while(false), so the early exits below are `break` rather than `yield break`.
            // The difference matters: `yield break` still runs the finally — the delete does go
            // out on the wire — but it then terminates the iterator, so the cleanup step the
            // finally just computed is never yielded. A run that failed at CreateLogGroup would
            // silently drop the news of whether its log group got deleted, which is the one thing
            // a page built to show the failures must not do. `break` leaves the loop, the finally
            // runs, and the yield after it is reached. Same shape as OciVaultDemo.
            do
            {
                bool logGroupCreated = false;

                yield return await RunStepAsync(
                    "CreateLogGroup",
                    $"POST {factory.ServiceUrl}/\nclient.CreateLogGroupAsync(new CreateLogGroupRequest {{ LogGroupName = \"{logGroupName}\" }})",
                    async () =>
                    {
                        logGroupAttempted = true;

                        CreateLogGroupResponse response = await client.CreateLogGroupAsync(
                            new CreateLogGroupRequest { LogGroupName = logGroupName }, ct).ConfigureAwait(false);

                        logGroupCreated = true;

                        return $"HTTP {(int)response.HttpStatusCode} — log group created";
                    }).ConfigureAwait(false);

                // One real fault should render as one red step, not four. The finally still runs, so
                // anything the creation left behind is still cleaned up.
                if (!logGroupCreated)
                {
                    break;
                }

                bool logStreamCreated = false;

                yield return await RunStepAsync(
                    "CreateLogStream",
                    $"POST {factory.ServiceUrl}/\nclient.CreateLogStreamAsync(new CreateLogStreamRequest {{ LogGroupName = \"{logGroupName}\", LogStreamName = \"{logStreamName}\" }})",
                    async () =>
                    {
                        CreateLogStreamResponse response = await client.CreateLogStreamAsync(
                            new CreateLogStreamRequest { LogGroupName = logGroupName, LogStreamName = logStreamName }, ct).ConfigureAwait(false);

                        logStreamCreated = true;

                        return $"HTTP {(int)response.HttpStatusCode} — log stream created";
                    }).ConfigureAwait(false);

                if (!logStreamCreated)
                {
                    break;
                }

                bool eventPut = false;

                yield return await RunStepAsync(
                    "PutLogEvents",
                    $"POST {factory.ServiceUrl}/\nclient.PutLogEventsAsync(new PutLogEventsRequest {{ LogGroupName = \"{logGroupName}\", LogStreamName = \"{logStreamName}\", LogEvents = [ new InputLogEvent {{ Message = \"{message}\" }} ] }})",
                    async () =>
                    {
                        PutLogEventsResponse response = await client.PutLogEventsAsync(
                            new PutLogEventsRequest
                            {
                                LogGroupName = logGroupName,
                                LogStreamName = logStreamName,
                                LogEvents = [new InputLogEvent { Timestamp = DateTime.UtcNow, Message = message }],
                            }, ct).ConfigureAwait(false);

                        eventPut = true;

                        return $"HTTP {(int)response.HttpStatusCode} — event ingested";
                    }).ConfigureAwait(false);

                if (!eventPut)
                {
                    break;
                }

                yield return await RunStepAsync(
                    "GetLogEvents",
                    $"POST {factory.ServiceUrl}/\nclient.GetLogEventsAsync(new GetLogEventsRequest {{ LogGroupName = \"{logGroupName}\", LogStreamName = \"{logStreamName}\" }})",
                    async () =>
                    {
                        // floci ingests synchronously, so the first read already has the event.
                        // Real CloudWatch Logs does not: PutLogEvents acks before the event is
                        // retrievable, so asserting off a single Describe would paint this step red
                        // on a perfectly healthy AWS account — and the page reaches real AWS
                        // whenever UseEmulator is false. See docs/BLAZOR-PLAN.md §14.
                        GetLogEventsResponse response;
                        List<OutputLogEvent> events;
                        OutputLogEvent? matched;

                        for (int attempt = 0; ; attempt++)
                        {
                            response = await client.GetLogEventsAsync(
                                new GetLogEventsRequest { LogGroupName = logGroupName, LogStreamName = logStreamName }, ct).ConfigureAwait(false);

                            // AWSSDK v4 leaves an absent collection null rather than empty (§14);
                            // a stream whose event has not surfaced yet is exactly that case, so
                            // the unguarded .Find would throw a bare NullReferenceException in
                            // place of the diagnostic message below.
                            events = response.Events ?? [];

                            // The round trip is only proven by the message this run wrote actually
                            // coming back, not by the call merely succeeding.
                            matched = events.Find(e => e.Message == message);

                            if (matched is not null)
                            {
                                break;
                            }

                            // An exhausted cap is a failure, never a success carrying whatever the
                            // last read happened to return (§14).
                            if (attempt == ReadBackPollAttempts - 1)
                            {
                                throw new InvalidOperationException(
                                    $"HTTP {(int)response.HttpStatusCode} — {events.Count} event(s) returned after {ReadBackPollAttempts} polls over "
                                    + $"{ReadBackPollAttempts * ReadBackPollDelay.TotalSeconds:0.#}s, none matching the message this run wrote.");
                            }

                            await Task.Delay(ReadBackPollDelay, ct).ConfigureAwait(false);
                        }

                        return $"HTTP {(int)response.HttpStatusCode} — {events.Count} event(s), including this run's message";
                    }).ConfigureAwait(false);
            }
            while (false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped
            // enumerating, so a re-run always starts from a clean account. The step it produces
            // is yielded below — an iterator may not yield from inside a finally. Deleting the
            // log group also deletes every stream and event inside it, so there is no separate
            // DeleteLogStream step to run.
            if (logGroupAttempted)
            {
                deleteLogGroupStep = await RunStepAsync(
                    "DeleteLogGroup — cleanup",
                    $"POST {factory.ServiceUrl}/\nclient.DeleteLogGroupAsync(new DeleteLogGroupRequest {{ LogGroupName = \"{logGroupName}\" }})",
                    async () =>
                    {
                        // CancellationToken.None throughout: this runs precisely when the caller
                        // has given up, and a cleanup that honoured ct would never delete anything
                        // on the cancelled runs that need it most.
                        DeleteLogGroupResponse response = await client.DeleteLogGroupAsync(
                            new DeleteLogGroupRequest { LogGroupName = logGroupName }, CancellationToken.None).ConfigureAwait(false);

                        return $"HTTP {(int)response.HttpStatusCode} — log group deleted"
                            + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                    }).ConfigureAwait(false);
            }
        }

        if (deleteLogGroupStep is not null)
        {
            yield return deleteLogGroupStep;
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
    /// the emulator does something real CloudWatch Logs would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still deletes the log
        // group. Catching it here would instead fabricate a "Failed" step for every remaining
        // operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
