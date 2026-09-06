using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.EventBridge;

/// <summary>
/// AWS EventBridge against floci. Ordinary AWSSDK.EventBridge code — the only emulator-aware line
/// in the sample is in <see cref="EventBridgeClientFactory"/>.
/// </summary>
public sealed class EventBridgeDemo(EventBridgeClientFactory factory) : IServiceDemo
{
    private const string Source = "flocilab.demo";

    public string Provider => CloudProvider.Aws;

    public string Slug => "eventbridge";

    public string DisplayName => "EventBridge";

    public string Category => "Messaging";

    public string Route => "/aws/eventbridge";

    /// <summary>ListEventBuses — one request, no state, and the cheapest call EventBridge has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonEventBridge client = factory.Create();
            ListEventBusesResponse response = await client.ListEventBusesAsync(new ListEventBusesRequest(), ct).ConfigureAwait(false);
            int count = response.EventBuses?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListEventBuses returned {count} bus(es).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonEventBridge client = factory.Create();

        // Unique per run, so two runs never collide and a leftover bus from a crashed run never
        // makes the next one fail.
        string suffix = Guid.NewGuid().ToString("N");
        string busName = $"flocilab-eventbridge-{suffix}";
        string ruleName = $"flocilab-rule-{suffix}";
        const string TargetId = "flocilab-target";

        // A target ARN this sample never creates — PutTargets records the association without
        // invoking it, which is enough to demonstrate the rule/target shape without a second
        // cloud package (constraint 1: EventBridge already has one, and a real target would need
        // AWSSDK.SQS).
        string targetArn = $"arn:aws:sqs:us-east-1:000000000000:flocilab-eventbridge-target-{suffix}";

        bool busCreated = false;
        bool ruleCreated = false;
        bool targetPut = false;

        DemoStep? removeTargetsStep = null;
        DemoStep? deleteRuleStep = null;
        DemoStep? deleteBusStep = null;

        try
        {
            yield return await RunStepAsync(
                "CreateEventBus",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.CreateEventBus\nclient.CreateEventBusAsync(new CreateEventBusRequest {{ Name = \"{busName}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the bus exists and cleanup has to know about it.
                    busCreated = true;
                    CreateEventBusResponse response = await client.CreateEventBusAsync(
                        new CreateEventBusRequest { Name = busName }, ct).ConfigureAwait(false);

                    return $"EventBusArn: {response.EventBusArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutRule",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.PutRule\nclient.PutRuleAsync(new PutRuleRequest {{ Name = \"{ruleName}\", EventBusName = \"{busName}\", EventPattern = \"{{\\\"source\\\":[\\\"{Source}\\\"]}}\", State = RuleState.ENABLED }})",
                async () =>
                {
                    ruleCreated = true;
                    PutRuleResponse response = await client.PutRuleAsync(
                        new PutRuleRequest
                        {
                            Name = ruleName,
                            EventBusName = busName,
                            EventPattern = $"{{\"source\":[\"{Source}\"]}}",
                            State = RuleState.ENABLED,
                        }, ct).ConfigureAwait(false);

                    return $"RuleArn: {response.RuleArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutTargets",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.PutTargets\nclient.PutTargetsAsync(new PutTargetsRequest {{ Rule = \"{ruleName}\", EventBusName = \"{busName}\", Targets = [new Target {{ Id = \"{TargetId}\", Arn = \"{targetArn}\" }}] }})",
                async () =>
                {
                    targetPut = true;
                    PutTargetsResponse response = await client.PutTargetsAsync(
                        new PutTargetsRequest
                        {
                            Rule = ruleName,
                            EventBusName = busName,
                            Targets = [new Target { Id = TargetId, Arn = targetArn }],
                        }, ct).ConfigureAwait(false);

                    // AWSSDK v4 made response scalars nullable, so an omitted FailedEntryCount
                    // arrives as null — and `null != 0` is true. Without the coalesce, a response
                    // that simply left the field out would be reported as the sample failing.
                    int failed = response.FailedEntryCount ?? 0;

                    if (failed != 0)
                    {
                        throw new InvalidOperationException($"PutTargets reported {failed} failed entrie(s). {DescribeFailures(response.FailedEntries?.Select(e => $"{e.TargetId}: {e.ErrorCode} {e.ErrorMessage}"))}");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — FailedEntryCount: {failed}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeRule",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.DescribeRule\nclient.DescribeRuleAsync(new DescribeRuleRequest {{ Name = \"{ruleName}\", EventBusName = \"{busName}\" }})",
                async () =>
                {
                    DescribeRuleResponse response = await client.DescribeRuleAsync(
                        new DescribeRuleRequest { Name = ruleName, EventBusName = busName }, ct).ConfigureAwait(false);

                    // A rule that did not round-trip its pattern or state did not round-trip. The
                    // lede promises this page shows what floci actually answered, so a mismatch
                    // goes out red rather than a green badge over a broken read.
                    if (response.State != RuleState.ENABLED || response.EventPattern?.Contains(Source, StringComparison.Ordinal) != true)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — State: {response.State}, EventPattern: {response.EventPattern}");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — State: {response.State}, EventPattern: {response.EventPattern}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutEvents",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.PutEvents\nclient.PutEventsAsync(new PutEventsRequest {{ Entries = [new PutEventsRequestEntry {{ Source = \"{Source}\", DetailType = \"flocilab.test\", Detail = \"{{}}\", EventBusName = \"{busName}\" }}] }})",
                async () =>
                {
                    PutEventsResponse response = await client.PutEventsAsync(
                        new PutEventsRequest
                        {
                            Entries =
                            [
                                new PutEventsRequestEntry
                                {
                                    Source = Source,
                                    DetailType = "flocilab.test",
                                    Detail = "{}",
                                    EventBusName = busName,
                                },
                            ],
                        }, ct).ConfigureAwait(false);

                    int failed = response.FailedEntryCount ?? 0;

                    if (failed != 0)
                    {
                        throw new InvalidOperationException($"PutEvents reported {failed} failed entrie(s). {DescribeFailures(response.Entries?.Where(e => e.ErrorCode is not null).Select(e => $"{e.ErrorCode} {e.ErrorMessage}"))}");
                    }

                    string eventId = response.Entries is [{ EventId: string id }, ..] ? id : "(none)";

                    return $"HTTP {(int)response.HttpStatusCode} — EventId: {eventId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListTargetsByRule",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.ListTargetsByRule\nclient.ListTargetsByRuleAsync(new ListTargetsByRuleRequest {{ Rule = \"{ruleName}\", EventBusName = \"{busName}\" }})",
                async () =>
                {
                    ListTargetsByRuleResponse response = await client.ListTargetsByRuleAsync(
                        new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName }, ct).ConfigureAwait(false);

                    int count = response.Targets?.Count ?? 0;

                    return $"HTTP {(int)response.HttpStatusCode} — {count} target(s)";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. Order matters: a rule cannot be
            // deleted while it still has targets, and a bus cannot be deleted while it still has
            // rules. The steps are yielded below — an iterator may not yield from inside a finally.
            if (targetPut)
            {
                removeTargetsStep = await RunStepAsync(
                    "RemoveTargets — cleanup",
                    $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.RemoveTargets\nclient.RemoveTargetsAsync(new RemoveTargetsRequest {{ Rule = \"{ruleName}\", EventBusName = \"{busName}\", Ids = [\"{TargetId}\"] }})",
                    async () =>
                    {
                        RemoveTargetsResponse response;

                        try
                        {
                            response = await client.RemoveTargetsAsync(
                                new RemoveTargetsRequest { Rule = ruleName, EventBusName = busName, Ids = [TargetId] }, CancellationToken.None).ConfigureAwait(false);
                        }
                        // targetPut is set before PutTargets, so a PutTargets that never landed
                        // still reaches here. Removing a target from a rule that was never created
                        // is a 404, and rendering that red would blame the emulator for a resource
                        // this run never made. (SqsDemo.DeleteQueueAsync takes the same line.)
                        catch (ResourceNotFoundException)
                        {
                            return "Nothing to remove — the rule was never created.";
                        }

                        // RemoveTargets answers 200 even when it removed nothing, so the count is
                        // the only signal. A green cleanup step that silently left the target
                        // attached is worse than a red one: on real AWS the DeleteRule below then
                        // fails with "Rule can't be deleted since it has targets".
                        int failed = response.FailedEntryCount ?? 0;

                        if (failed != 0)
                        {
                            throw new InvalidOperationException($"RemoveTargets reported {failed} failed entrie(s). {DescribeFailures(response.FailedEntries?.Select(e => $"{e.TargetId}: {e.ErrorCode} {e.ErrorMessage}"))}");
                        }

                        return $"HTTP {(int)response.HttpStatusCode} — FailedEntryCount: {failed}"
                            + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                    }).ConfigureAwait(false);
            }

            if (ruleCreated)
            {
                deleteRuleStep = await RunStepAsync(
                    "DeleteRule — cleanup",
                    $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.DeleteRule\nclient.DeleteRuleAsync(new DeleteRuleRequest {{ Name = \"{ruleName}\", EventBusName = \"{busName}\" }})",
                    async () =>
                    {
                        DeleteRuleResponse response;

                        try
                        {
                            response = await client.DeleteRuleAsync(
                                new DeleteRuleRequest { Name = ruleName, EventBusName = busName }, CancellationToken.None).ConfigureAwait(false);
                        }
                        // ruleCreated is claimed before PutRule, so a PutRule that never landed
                        // still reaches here — and DeleteRule on a rule that does not exist is a
                        // 404, unlike DeleteEventBus below, which is idempotent.
                        catch (ResourceNotFoundException)
                        {
                            return "Nothing to remove — the rule was never created.";
                        }

                        return $"HTTP {(int)response.HttpStatusCode} — removed the rule"
                            + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                    }).ConfigureAwait(false);
            }

            if (busCreated)
            {
                deleteBusStep = await RunStepAsync(
                    "DeleteEventBus — cleanup",
                    $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSEvents.DeleteEventBus\nclient.DeleteEventBusAsync(new DeleteEventBusRequest {{ Name = \"{busName}\" }})",
                    async () =>
                    {
                        DeleteEventBusResponse response = await client.DeleteEventBusAsync(
                            new DeleteEventBusRequest { Name = busName }, CancellationToken.None).ConfigureAwait(false);

                        return $"HTTP {(int)response.HttpStatusCode} — removed the bus"
                            + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                    }).ConfigureAwait(false);
            }
        }

        if (removeTargetsStep is not null)
        {
            yield return removeTargetsStep;
        }

        if (deleteRuleStep is not null)
        {
            yield return deleteRuleStep;
        }

        if (deleteBusStep is not null)
        {
            yield return deleteBusStep;
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
    /// the emulator does something real EventBridge would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the bus,
        // rule and target. Catching it here would instead fabricate a "Failed" step for every
        // remaining operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// EventBridge reports batch failures per entry rather than as a status code, so the reason is
    /// only ever in the entry list. Reporting the count alone gives the page nothing to debug with.
    /// </summary>
    private static string DescribeFailures(IEnumerable<string>? reasons)
    {
        string[] listed = reasons?.ToArray() ?? [];

        return listed.Length == 0 ? "(the response carried no reason)" : string.Join("; ", listed);
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
