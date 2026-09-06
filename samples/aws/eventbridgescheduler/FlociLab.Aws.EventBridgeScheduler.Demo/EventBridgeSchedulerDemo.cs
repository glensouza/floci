using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.Scheduler;
using Amazon.Scheduler.Model;
using FlociLab.Core;

namespace FlociLab.Aws.EventBridgeScheduler;

/// <summary>
/// AWS EventBridge Scheduler against floci. Ordinary AWSSDK.Scheduler code — the only
/// emulator-aware line in the sample is in <see cref="EventBridgeSchedulerClientFactory"/>.
/// </summary>
public sealed class EventBridgeSchedulerDemo(EventBridgeSchedulerClientFactory factory) : IServiceDemo
{
    // The rate UpdateSchedule moves the schedule to, named once because the update step now both
    // sends it and asserts the read-back matches.
    private const string UpdatedExpression = "rate(10 minutes)";

    public string Provider => CloudProvider.Aws;

    public string Slug => "eventbridgescheduler";

    public string DisplayName => "EventBridge Scheduler";

    public string Category => "Messaging";

    public string Route => "/aws/eventbridgescheduler";

    /// <summary>ListSchedules — one request, no state, and the cheapest call Scheduler has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonScheduler client = factory.Create();
            ListSchedulesResponse response = await client.ListSchedulesAsync(new ListSchedulesRequest(), ct).ConfigureAwait(false);
            int count = response.Schedules?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListSchedules returned {count} schedule(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonScheduler client = factory.Create();

        // Unique per run, so two runs never collide and a leftover schedule from a crashed run
        // never makes the next one fail.
        string suffix = Guid.NewGuid().ToString("N");
        string scheduleName = $"flocilab-schedule-{suffix}";

        // A target and a role that do not exist. floci accepts both as opaque strings, which is
        // what lets the schedule shape be demonstrated without a second cloud package
        // (constraint 1: a real target would need AWSSDK.SQS, and a real role
        // AWSSDK.IdentityManagement).
        //
        // This is an emulator-only affordance, and the page has a real-AWS mode, so be exact about
        // the two ways it diverges (both in docs/BLAZOR-PLAN.md §14, probed 2026-09-06):
        //
        //   * floci's scheduler really does fire — a rate(1 minute) schedule delivered to a real
        //     SQS queue in ~50 s — and it fires without checking the role at all. This run never
        //     reaches that point: rate(5 minutes) is far longer than the round trip, and the
        //     finally below deletes the schedule whether the run succeeded, failed or was
        //     cancelled. So nothing here ever dereferences the ARNs; the scheduler behind them
        //     would.
        //   * Real EventBridge Scheduler validates the execution role at CreateSchedule and
        //     answers ValidationException for one it cannot assume, so against real AWS
        //     (UseEmulator=false, the red "REAL AWS" badge) this run fails at step 1 — the same
        //     shape the neighbouring Pipes sample records, not the opposite of it.
        string targetArn = $"arn:aws:sqs:{factory.Region}:000000000000:flocilab-scheduler-target-{suffix}";
        string roleArn = "arn:aws:iam::000000000000:role/flocilab-scheduler-role";

        bool scheduleCreated = false;

        DemoStep? deleteScheduleStep = null;

        try
        {
            yield return await RunStepAsync(
                "CreateSchedule",
                $"POST {factory.ServiceUrl}/schedules/{scheduleName}\nclient.CreateScheduleAsync(new CreateScheduleRequest {{ Name = \"{scheduleName}\", ScheduleExpression = \"rate(5 minutes)\", FlexibleTimeWindow = new FlexibleTimeWindow {{ Mode = FlexibleTimeWindowMode.OFF }}, Target = new Target {{ Arn = \"{targetArn}\", RoleArn = \"{roleArn}\" }} }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the schedule exists and cleanup has to know about it.
                    scheduleCreated = true;
                    CreateScheduleResponse response = await client.CreateScheduleAsync(
                        new CreateScheduleRequest
                        {
                            Name = scheduleName,
                            ScheduleExpression = "rate(5 minutes)",
                            FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF },
                            Target = new Target { Arn = targetArn, RoleArn = roleArn },
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — ScheduleArn: {response.ScheduleArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetSchedule",
                $"GET {factory.ServiceUrl}/schedules/{scheduleName}\nclient.GetScheduleAsync(new GetScheduleRequest {{ Name = \"{scheduleName}\" }})",
                async () =>
                {
                    GetScheduleResponse response = await client.GetScheduleAsync(
                        new GetScheduleRequest { Name = scheduleName }, ct).ConfigureAwait(false);

                    // A schedule that did not round-trip its target did not round-trip. The lede
                    // promises this page shows what floci actually answered, so a mismatch goes
                    // out red rather than a green badge over a broken read.
                    if (response.Target?.Arn != targetArn)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — Target.Arn: {response.Target?.Arn}");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — State: {response.State}, ScheduleExpression: {response.ScheduleExpression}, Target.Arn: {response.Target.Arn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListSchedules",
                $"GET {factory.ServiceUrl}/schedules?NamePrefix={scheduleName}\nclient.ListSchedulesAsync(new ListSchedulesRequest {{ NamePrefix = \"{scheduleName}\" }})",
                async () =>
                {
                    // NamePrefix, not an unfiltered list: real Scheduler pages ListSchedules at 100
                    // per call and this code follows no NextToken, so against an account with more
                    // than a page of schedules the one this run just created could simply be on
                    // page 2 — and the containment check below would then render red and blame the
                    // service for a correct listing. The prefix makes the assertion exact and
                    // page-independent. It is also a real Scheduler feature the neighbouring Pipes
                    // sample had no equivalent of.
                    ListSchedulesResponse response = await client.ListSchedulesAsync(
                        new ListSchedulesRequest { NamePrefix = scheduleName }, ct).ConfigureAwait(false);

                    List<ScheduleSummary> schedules = response.Schedules ?? [];

                    // A listing that does not contain the schedule this run provably just created
                    // has not listed it, however many other schedules came back. Returning the
                    // bare count instead would paint an empty listing green — the shape this repo
                    // has now found in every list step it did not assert.
                    if (!schedules.Any(s => s.Name == scheduleName))
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — {schedules.Count} schedule(s) matching the prefix, none of them {scheduleName}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {schedules.Count} schedule(s) matching the prefix, including {scheduleName}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateSchedule",
                $"PUT {factory.ServiceUrl}/schedules/{scheduleName}\nclient.UpdateScheduleAsync(new UpdateScheduleRequest {{ Name = \"{scheduleName}\", ScheduleExpression = \"{UpdatedExpression}\", FlexibleTimeWindow = new FlexibleTimeWindow {{ Mode = FlexibleTimeWindowMode.OFF }}, Target = new Target {{ Arn = \"{targetArn}\", RoleArn = \"{roleArn}\" }} }})\n\nGET {factory.ServiceUrl}/schedules/{scheduleName}\nclient.GetScheduleAsync(new GetScheduleRequest {{ Name = \"{scheduleName}\" }})",
                async () =>
                {
                    UpdateScheduleResponse response = await client.UpdateScheduleAsync(
                        new UpdateScheduleRequest
                        {
                            Name = scheduleName,
                            ScheduleExpression = UpdatedExpression,
                            FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF },
                            Target = new Target { Arn = targetArn, RoleArn = roleArn },
                        }, ct).ConfigureAwait(false);

                    // UpdateSchedule echoes the same ScheduleArn whether or not the PUT changed
                    // anything, so the response alone cannot tell an applied update from an
                    // ignored one — and this is the last step that touches the schedule, so
                    // nothing downstream would catch it either. Both requests are shown above
                    // rather than only the PUT: the page promises the request beside a step is
                    // what actually went over the wire.
                    GetScheduleResponse readBack = await client.GetScheduleAsync(
                        new GetScheduleRequest { Name = scheduleName }, ct).ConfigureAwait(false);

                    if (readBack.ScheduleExpression != UpdatedExpression)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — the update was accepted but the schedule still reads {readBack.ScheduleExpression}, not {UpdatedExpression}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — ScheduleArn: {response.ScheduleArn}\nRead back — ScheduleExpression: {readBack.ScheduleExpression}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            if (scheduleCreated)
            {
                deleteScheduleStep = await RunStepAsync(
                    "DeleteSchedule — cleanup",
                    $"DELETE {factory.ServiceUrl}/schedules/{scheduleName}\nclient.DeleteScheduleAsync(new DeleteScheduleRequest {{ Name = \"{scheduleName}\" }})",
                    async () =>
                    {
                        DeleteScheduleResponse response;

                        try
                        {
                            response = await client.DeleteScheduleAsync(
                                new DeleteScheduleRequest { Name = scheduleName }, CancellationToken.None).ConfigureAwait(false);
                        }
                        // scheduleCreated is set before CreateSchedule, so a CreateSchedule that
                        // never landed still reaches here. Deleting a schedule that does not exist
                        // is a 404 — floci and real Scheduler agree, so this is proof nothing was
                        // created rather than a delete that silently removed nothing, which is
                        // what makes the green badge below truthful.
                        catch (ResourceNotFoundException)
                        {
                            return "Nothing to remove — the schedule was never created.";
                        }

                        return $"HTTP {(int)response.HttpStatusCode} — removed the schedule"
                            + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                    }).ConfigureAwait(false);
            }
        }

        if (deleteScheduleStep is not null)
        {
            yield return deleteScheduleStep;
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
    /// the emulator does something real EventBridge Scheduler would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the
        // schedule. Catching it here would instead fabricate a "Failed" step for every remaining
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
