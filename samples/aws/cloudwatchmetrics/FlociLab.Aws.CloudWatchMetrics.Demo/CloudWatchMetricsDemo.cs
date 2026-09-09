using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.Runtime;
using FlociLab.Core;
// Amazon.Runtime (above, for AmazonServiceException) has its own Metric type; this is the one the SDK returns.
using Metric = Amazon.CloudWatch.Model.Metric;

namespace FlociLab.Aws.CloudWatchMetrics;

/// <summary>
/// Amazon CloudWatch Metrics against floci. Ordinary AWSSDK.CloudWatch code — the only
/// emulator-aware line in the sample is in <see cref="CloudWatchMetricsClientFactory"/>.
/// </summary>
public sealed class CloudWatchMetricsDemo(CloudWatchMetricsClientFactory factory) : IServiceDemo
{
    // Real CloudWatch aggregates a PutMetricData call into its period bucket before it is
    // queryable — typically under a minute, but not zero — so against real AWS the first read can
    // miss. floci ingests synchronously, so against the emulator the first read always hits and
    // these never cost anything.
    private static readonly TimeSpan ReadBackPollDelay = TimeSpan.FromMilliseconds(500);

    // Sized to the aggregation window described above rather than copied from the CloudWatch Logs
    // sample, whose ingestion is far faster: its 10 s budget would exhaust on a healthy real-AWS
    // account and paint red exactly the false failure this polling exists to prevent.
    private static readonly TimeSpan ReadBackPollBudget = TimeSpan.FromSeconds(60);

    // N attempts have N-1 waits between them, so the +1 is what makes the budget above the real
    // elapsed time rather than one delay short of it.
    private static readonly int ReadBackPollAttempts = (int)(ReadBackPollBudget / ReadBackPollDelay) + 1;

    private const string Namespace = "FlociLab/CloudWatchMetrics";

    private const string MetricName = "ProbeMetric";

    public string Provider => CloudProvider.Aws;

    public string Slug => "cloudwatchmetrics";

    public string DisplayName => "CloudWatch Metrics";

    public string Category => "Observability";

    public string Route => "/aws/cloudwatchmetrics";

    /// <summary>ListMetrics — one request, no state, and the cheapest call the service has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonCloudWatch client = factory.Create();
            ListMetricsResponse response = await client.ListMetricsAsync(new ListMetricsRequest(), ct).ConfigureAwait(false);

            // AWSSDK v4 leaves an absent response collection null rather than empty (§14), and an
            // account with no custom metrics is the normal state of a fresh emulator — so an
            // unguarded .Count would throw here and Classify would report a healthy service as
            // Error.
            int count = response.Metrics?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListMetrics returned {count} metric(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonCloudWatch client = factory.Create();

        // Unique per run, so two runs never collide — CloudWatch has no DeleteMetric API (metrics
        // simply age out on their own retention schedule), so uniqueness rather than cleanup is
        // what keeps a re-run from reading back a previous run's datapoint.
        string runId = Guid.NewGuid().ToString("N");
        double value = Random.Shared.Next(1, 1000);

        // A do/while(false), so the early exits below are `break` rather than trying to thread a
        // chain of booleans through three sequential yields. One real fault should render as one
        // red step, not three — a stopped emulator that fails PutMetricData should not also fail
        // GetMetricStatistics and ListMetrics for the same underlying reason.
        //
        // GetMetricStatistics runs BEFORE ListMetrics, which is not the order the CloudWatch Logs
        // sample this was cloned from uses. There every break guarded a genuine prerequisite — no
        // event without a stream — whereas ListMetrics is not a prerequisite for reading
        // statistics: the two are independent reads of the same datapoint. Keeping the cloned
        // order gated the authoritative read-back behind the discovery call, and real CloudWatch
        // takes up to 15 minutes to surface a new metric in ListMetrics while serving its
        // statistics far sooner (docs/BLAZOR-PLAN.md §14) — so on real AWS the run aborted at
        // ListMetrics and never reached the step written to tolerate exactly that latency.
        do
        {
            bool published = false;

            yield return await RunStepAsync(
                "PutMetricData",
                $"POST {factory.ServiceUrl}/\nclient.PutMetricDataAsync(new PutMetricDataRequest {{ Namespace = \"{Namespace}\", MetricData = [ new MetricDatum {{ MetricName = \"{MetricName}\", Value = {value}, Unit = Count, Dimensions = [ new Dimension {{ Name = \"RunId\", Value = \"{runId}\" }} ] }} ] }})",
                async () =>
                {
                    PutMetricDataResponse response = await client.PutMetricDataAsync(
                        new PutMetricDataRequest
                        {
                            Namespace = Namespace,
                            MetricData =
                            [
                                new MetricDatum
                                {
                                    MetricName = MetricName,
                                    Value = value,
                                    Unit = StandardUnit.Count,
                                    Timestamp = DateTime.UtcNow,
                                    Dimensions = [new Dimension { Name = "RunId", Value = runId }],
                                },
                            ],
                        }, ct).ConfigureAwait(false);

                    published = true;

                    return $"HTTP {(int)response.HttpStatusCode} — datapoint published";
                }).ConfigureAwait(false);

            if (!published)
            {
                break;
            }

            bool statisticsRead = false;

            yield return await RunStepAsync(
                "GetMetricStatistics",
                $"POST {factory.ServiceUrl}/\nclient.GetMetricStatisticsAsync(new GetMetricStatisticsRequest {{ Namespace = \"{Namespace}\", MetricName = \"{MetricName}\", Dimensions = [ new Dimension {{ Name = \"RunId\", Value = \"{runId}\" }} ], Statistics = [ \"Sum\" ] }})",
                async () =>
                {
                    DateTime windowStart = DateTime.UtcNow.AddMinutes(-10);

                    // floci aggregates a Put into its statistics immediately, but real CloudWatch
                    // does not — a Put acks before the datapoint is queryable, so asserting off a
                    // single read would paint this step red on a perfectly healthy AWS account,
                    // and the page reaches real AWS whenever UseEmulator is false. See
                    // docs/BLAZOR-PLAN.md §14.
                    GetMetricStatisticsResponse response;
                    List<Datapoint> datapoints;

                    for (int attempt = 0; ; attempt++)
                    {
                        response = await client.GetMetricStatisticsAsync(
                            new GetMetricStatisticsRequest
                            {
                                Namespace = Namespace,
                                MetricName = MetricName,
                                Dimensions = [new Dimension { Name = "RunId", Value = runId }],
                                StartTime = windowStart,
                                EndTime = DateTime.UtcNow.AddMinutes(1),
                                Period = 60,
                                Statistics = [Statistic.Sum],
                            }, ct).ConfigureAwait(false);

                        // AWSSDK v4 leaves an absent collection null rather than empty (§14); a
                        // period whose datapoint has not surfaced yet is exactly that case, so the
                        // unguarded .Find would throw a bare NullReferenceException in place of the
                        // diagnostic message below.
                        datapoints = response.Datapoints ?? [];

                        // The round trip is only proven by the value this run wrote actually
                        // coming back, not by the call merely succeeding.
                        if (datapoints.Exists(d => d.Sum is { } sum && Math.Abs((double)sum - value) < 0.001))
                        {
                            break;
                        }

                        // An exhausted cap is a failure, never a success carrying whatever the last
                        // read happened to return (§14).
                        if (attempt == ReadBackPollAttempts - 1)
                        {
                            throw new InvalidOperationException(
                                $"HTTP {(int)response.HttpStatusCode} — {datapoints.Count} datapoint(s) returned after {ReadBackPollAttempts} polls over "
                                + $"{ReadBackPollBudget.TotalSeconds:0.#}s, none matching the value this run wrote.");
                        }

                        await Task.Delay(ReadBackPollDelay, ct).ConfigureAwait(false);
                    }

                    statisticsRead = true;

                    return $"HTTP {(int)response.HttpStatusCode} — {datapoints.Count} datapoint(s), including this run's value ({value:0})";
                }).ConfigureAwait(false);

            if (!statisticsRead)
            {
                break;
            }

            // Last, and nothing is gated behind it. ListMetrics is the discovery call rather than
            // the proof — the round trip is already established above — so its own latency can no
            // longer suppress anything.
            yield return await RunStepAsync(
                "ListMetrics",
                $"POST {factory.ServiceUrl}/\nclient.ListMetricsAsync(new ListMetricsRequest {{ Namespace = \"{Namespace}\", MetricName = \"{MetricName}\", Dimensions = [ new DimensionFilter {{ Name = \"RunId\", Value = \"{runId}\" }} ] }})",
                async () =>
                {
                    ListMetricsResponse response;
                    List<Metric> metrics;

                    for (int attempt = 0; ; attempt++)
                    {
                        response = await client.ListMetricsAsync(
                            new ListMetricsRequest
                            {
                                Namespace = Namespace,
                                MetricName = MetricName,
                                Dimensions = [new DimensionFilter { Name = "RunId", Value = runId }],
                            }, ct).ConfigureAwait(false);

                        // AWSSDK v4 leaves an absent collection null rather than empty (§14).
                        metrics = response.Metrics ?? [];

                        if (metrics.Count != 0)
                        {
                            break;
                        }

                        // An exhausted cap is a failure, never a success carrying an empty listing
                        // (docs/BLAZOR-PLAN.md §14). Against real AWS this genuinely can exhaust:
                        // the metric is guaranteed to appear only within 15 minutes, which is far
                        // longer than a demo page should sit blocked, so the message says so
                        // rather than leaving a viewer to conclude the publish failed.
                        if (attempt == ReadBackPollAttempts - 1)
                        {
                            throw new InvalidOperationException(
                                $"HTTP {(int)response.HttpStatusCode} — 0 metric(s) matched this run's RunId dimension after "
                                + $"{ReadBackPollBudget.TotalSeconds:0.#}s. Against real CloudWatch a new metric can take up to 15 minutes to "
                                + "surface in ListMetrics; the datapoint above proves it was published regardless.");
                        }

                        await Task.Delay(ReadBackPollDelay, ct).ConfigureAwait(false);
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {metrics.Count} metric(s) matched this run's RunId dimension";
                }).ConfigureAwait(false);
        }
        while (false);
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
    /// the emulator does something real CloudWatch would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached.
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
