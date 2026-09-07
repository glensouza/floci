using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.SimpleWorkflow;
using Amazon.SimpleWorkflow.Model;
using FlociLab.Core;

namespace FlociLab.Aws.Swf;

/// <summary>
/// AWS Simple Workflow Service against floci. Ordinary AWSSDK.SimpleWorkflow code — the only
/// emulator-aware line in the sample is in <see cref="SwfClientFactory"/>. Unlike Step Functions,
/// SWF has no server-side state machine to execute: this sample plays both roles a production
/// system would split across two processes — it starts the execution and then polls and answers
/// its own decision task, the way a minimal decider worker would.
/// </summary>
public sealed class SwfDemo(SwfClientFactory factory) : IServiceDemo
{
    private const string WorkflowTypeVersion = "1.0";

    // floci answers both of the reads below correctly on the first try. Real SWF applies a
    // decision asynchronously and its visibility listings are eventually consistent, so a
    // single-shot assertion paints a healthy run red (docs/BLAZOR-PLAN.md §14).
    private const int ExecutionPollAttempts = 10;

    private static readonly TimeSpan ExecutionPollDelay = TimeSpan.FromMilliseconds(500);

    public string Provider => CloudProvider.Aws;

    public string Slug => "swf";

    public string DisplayName => "SWF";

    public string Category => "Workflows";

    public string Route => "/aws/swf";

    /// <summary>ListDomains — one request, no state, and the cheapest call SWF has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonSimpleWorkflow client = factory.Create();
            ListDomainsResponse response = await client.ListDomainsAsync(
                new ListDomainsRequest { RegistrationStatus = RegistrationStatus.REGISTERED }, ct).ConfigureAwait(false);
            int count = response.DomainInfos.Infos?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListDomains returned {count} domain(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonSimpleWorkflow client = factory.Create();

        // Unique per run, so two runs never collide. This matters more for SWF than for most
        // services: a domain name can never be reused once deprecated (§14), so the cleanup step
        // below permanently burns whatever name this run picks.
        string suffix = Guid.NewGuid().ToString("N");
        string domainName = $"flocilab-swf-{suffix}";
        string workflowTypeName = $"flocilab-workflow-{suffix}";
        string workflowId = $"flocilab-run-{suffix}";
        string taskListName = $"flocilab-tasklist-{suffix}";

        bool domainAttempted = false;
        DemoStep? deprecateDomainStep = null;

        // Declared out here rather than inside the try so the cleanup in the finally can read it.
        // A cleanup that re-derives "did this land?" from a listing gets the answer wrong whenever
        // the listing is stale, and for SWF that means leaking a name nothing can ever reclaim.
        bool domainRegistered = false;

        try
        {
            yield return await RunStepAsync(
                "RegisterDomain",
                $"POST {factory.ServiceUrl}/\nclient.RegisterDomainAsync(new RegisterDomainRequest {{ Name = \"{domainName}\", WorkflowExecutionRetentionPeriodInDays = \"1\" }})",
                async () =>
                {
                    // Set before the call, not after (docs/BLAZOR-PLAN.md §14): cleanup is gated
                    // on "the request was issued", never on "the response said yes", the same
                    // reasoning Step Functions' CreateStateMachine step uses.
                    domainAttempted = true;

                    RegisterDomainResponse response = await client.RegisterDomainAsync(
                        new RegisterDomainRequest
                        {
                            Name = domainName,
                            WorkflowExecutionRetentionPeriodInDays = "1",
                        }, ct).ConfigureAwait(false);

                    domainRegistered = true;

                    return $"HTTP {(int)response.HttpStatusCode} — domain registered";
                }).ConfigureAwait(false);

            // One real fault should render as one red step, not six. The finally still runs, so
            // anything the registration left behind is still cleaned up.
            if (!domainRegistered)
            {
                yield break;
            }

            bool workflowTypeRegistered = false;

            yield return await RunStepAsync(
                "RegisterWorkflowType",
                $"POST {factory.ServiceUrl}/\nclient.RegisterWorkflowTypeAsync(new RegisterWorkflowTypeRequest {{ Domain = \"{domainName}\", Name = \"{workflowTypeName}\", Version = \"{WorkflowTypeVersion}\" }})",
                async () =>
                {
                    RegisterWorkflowTypeResponse response = await client.RegisterWorkflowTypeAsync(
                        new RegisterWorkflowTypeRequest
                        {
                            Domain = domainName,
                            Name = workflowTypeName,
                            Version = WorkflowTypeVersion,
                        }, ct).ConfigureAwait(false);

                    workflowTypeRegistered = true;

                    return $"HTTP {(int)response.HttpStatusCode} — workflow type registered";
                }).ConfigureAwait(false);

            // Same reasoning as the domain gate: without this, a failed registration falls into
            // StartWorkflowExecution, which faults UnknownResourceFault and turns one real fault
            // into a second, derivative red step.
            if (!workflowTypeRegistered)
            {
                yield break;
            }

            string? runId = null;

            yield return await RunStepAsync(
                "StartWorkflowExecution",
                $"POST {factory.ServiceUrl}/\nclient.StartWorkflowExecutionAsync(new StartWorkflowExecutionRequest {{ Domain = \"{domainName}\", WorkflowId = \"{workflowId}\", WorkflowType = {{ Name = \"{workflowTypeName}\", Version = \"{WorkflowTypeVersion}\" }}, TaskList = {{ Name = \"{taskListName}\" }} }})",
                async () =>
                {
                    // The three timeout/policy fields have no defaults on the workflow type
                    // registered above, so SWF requires them here — floci rejects the request
                    // with DefaultUndefinedFault otherwise. Probed against floci 1.7.0, 2026-09-06.
                    StartWorkflowExecutionResponse response = await client.StartWorkflowExecutionAsync(
                        new StartWorkflowExecutionRequest
                        {
                            Domain = domainName,
                            WorkflowId = workflowId,
                            WorkflowType = new WorkflowType { Name = workflowTypeName, Version = WorkflowTypeVersion },
                            TaskList = new TaskList { Name = taskListName },
                            ExecutionStartToCloseTimeout = "60",
                            TaskStartToCloseTimeout = "30",
                            ChildPolicy = ChildPolicy.TERMINATE,
                            Input = "{}",
                        }, ct).ConfigureAwait(false);

                    runId = response.Run.RunId;

                    return $"HTTP {(int)response.HttpStatusCode} — RunId: {runId}";
                }).ConfigureAwait(false);

            if (runId is null)
            {
                yield break;
            }

            string? taskToken = null;

            yield return await RunStepAsync(
                "PollForDecisionTask",
                $"POST {factory.ServiceUrl}/\nclient.PollForDecisionTaskAsync(new PollForDecisionTaskRequest {{ Domain = \"{domainName}\", TaskList = {{ Name = \"{taskListName}\" }} }})",
                async () =>
                {
                    // Real SWF long-polls here — up to 60 s if nothing is scheduled. floci answers
                    // immediately because this run's own StartWorkflowExecution just scheduled the
                    // only decision task on this task list, so there is nothing to wait on.
                    PollForDecisionTaskResponse response = await client.PollForDecisionTaskAsync(
                        new PollForDecisionTaskRequest
                        {
                            Domain = domainName,
                            TaskList = new TaskList { Name = taskListName },
                        }, ct).ConfigureAwait(false);

                    taskToken = response.DecisionTask.TaskToken;

                    if (string.IsNullOrEmpty(taskToken))
                    {
                        throw new InvalidOperationException(
                            "PollForDecisionTask returned no task — nothing was waiting on the task list.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {response.DecisionTask.Events.Count} history event(s), decision task token received";
                }).ConfigureAwait(false);

            if (taskToken is null)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "RespondDecisionTaskCompleted",
                $"POST {factory.ServiceUrl}/\nclient.RespondDecisionTaskCompletedAsync(new RespondDecisionTaskCompletedRequest {{ TaskToken = \"(from poll)\", Decisions = [ CompleteWorkflowExecution(Result: \"ok\") ] }})",
                async () =>
                {
                    // Playing the decider: the only decision this workflow ever makes is to
                    // complete itself, which is what makes this sample not need a second cloud
                    // package (constraint 1) — a real decider would schedule an activity task
                    // against some other service instead.
                    RespondDecisionTaskCompletedResponse response = await client.RespondDecisionTaskCompletedAsync(
                        new RespondDecisionTaskCompletedRequest
                        {
                            TaskToken = taskToken,
                            Decisions =
                            [
                                new Decision
                                {
                                    DecisionType = DecisionType.CompleteWorkflowExecution,
                                    CompleteWorkflowExecutionDecisionAttributes = new CompleteWorkflowExecutionDecisionAttributes { Result = "ok" },
                                },
                            ],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — CompleteWorkflowExecution decision submitted";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeWorkflowExecution",
                $"POST {factory.ServiceUrl}/\nclient.DescribeWorkflowExecutionAsync(new DescribeWorkflowExecutionRequest {{ Domain = \"{domainName}\", Execution = {{ WorkflowId = \"{workflowId}\", RunId = \"{runId}\" }} }})",
                async () =>
                {
                    // floci closes the execution the instant the decision is accepted, so against
                    // the emulator the first read is the last. Real SWF applies a decision
                    // asynchronously, so asserting off a single response would paint a healthy
                    // account red — the defect Step Functions' poll loop already exists to avoid.
                    DescribeWorkflowExecutionResponse response;
                    WorkflowExecutionInfo info;

                    for (int attempt = 0; ; attempt++)
                    {
                        response = await client.DescribeWorkflowExecutionAsync(
                            new DescribeWorkflowExecutionRequest
                            {
                                Domain = domainName,
                                Execution = new WorkflowExecution { WorkflowId = workflowId, RunId = runId },
                            }, ct).ConfigureAwait(false);

                        info = response.WorkflowExecutionDetail.ExecutionInfo;

                        if (info.ExecutionStatus == ExecutionStatus.CLOSED)
                        {
                            break;
                        }

                        if (attempt == ExecutionPollAttempts - 1)
                        {
                            throw new InvalidOperationException(
                                $"Still {info.ExecutionStatus} after {ExecutionPollAttempts} polls over "
                                + $"{ExecutionPollAttempts * ExecutionPollDelay.TotalSeconds:0.#}s.");
                        }

                        await Task.Delay(ExecutionPollDelay, ct).ConfigureAwait(false);
                    }

                    // Closed, but closed how: a terminated, failed or timed-out execution is
                    // CLOSED too, and only COMPLETED means the decision this page submitted did
                    // what the step claims it did.
                    if (info.CloseStatus != CloseStatus.COMPLETED)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — ExecutionStatus: {info.ExecutionStatus}, "
                            + $"CloseStatus: {info.CloseStatus}, expected CLOSED/COMPLETED.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — ExecutionStatus: {info.ExecutionStatus}, CloseStatus: {info.CloseStatus}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListClosedWorkflowExecutions",
                $"POST {factory.ServiceUrl}/\nclient.ListClosedWorkflowExecutionsAsync(new ListClosedWorkflowExecutionsRequest {{ Domain = \"{domainName}\", StartTimeFilter = {{ OldestDate = <5 min ago> }} }})",
                async () =>
                {
                    // Two real-SWF behaviours floci does not have, either of which would render a
                    // healthy run red. The visibility listing is eventually consistent, hence the
                    // retry; and it is paginated, so on an account with history the execution this
                    // run just closed can sit behind a NextPageToken rather than on page one.
                    int total = 0;
                    bool found = false;
                    HttpStatusCode status = HttpStatusCode.OK;

                    for (int attempt = 0; attempt < ExecutionPollAttempts && !found; attempt++)
                    {
                        if (attempt > 0)
                        {
                            await Task.Delay(ExecutionPollDelay, ct).ConfigureAwait(false);
                        }

                        // Recounted per attempt, so the number reported is one listing's worth
                        // rather than the same executions counted once per poll.
                        total = 0;
                        string? pageToken = null;

                        do
                        {
                            ListClosedWorkflowExecutionsResponse response = await client.ListClosedWorkflowExecutionsAsync(
                                new ListClosedWorkflowExecutionsRequest
                                {
                                    Domain = domainName,
                                    StartTimeFilter = new ExecutionTimeFilter { OldestDate = DateTime.UtcNow.AddMinutes(-5) },
                                    NextPageToken = pageToken,
                                }, ct).ConfigureAwait(false);

                            List<WorkflowExecutionInfo> page = response.WorkflowExecutionInfos.ExecutionInfos ?? [];

                            total += page.Count;
                            found = found || page.Any(e => e.Execution.RunId == runId);
                            status = response.HttpStatusCode;
                            pageToken = string.IsNullOrEmpty(response.WorkflowExecutionInfos.NextPageToken)
                                ? null
                                : response.WorkflowExecutionInfos.NextPageToken;
                        }
                        while (pageToken is not null && !found);
                    }

                    // A listing that does not contain the execution this run provably just closed
                    // has not listed it, however many other executions came back.
                    if (!found)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)status} — {total} closed execution(s) after {ExecutionPollAttempts} polls, none of them this run.");
                    }

                    return $"HTTP {(int)status} — {total} closed execution(s), including this run's";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped
            // enumerating, so a re-run always starts from a clean account. The step it produces
            // is yielded below — an iterator may not yield from inside a finally.
            if (domainAttempted)
            {
                deprecateDomainStep = await RunStepAsync(
                    "DeprecateDomain — cleanup",
                    $"POST {factory.ServiceUrl}/\nclient.DeprecateDomainAsync(new DeprecateDomainRequest {{ Name = \"{domainName}\" }})",
                    async () =>
                    {
                        // Real SWF has no DeleteDomain — deprecating is the only teardown a
                        // domain ever gets, and a deprecated name can never be registered again
                        // (§14). CancellationToken.None throughout: this runs precisely when the
                        // caller has given up, and a cleanup that honoured ct would never
                        // deprecate anything on the cancelled runs that need it most.
                        // Only ask the server when this run does not already know the answer.
                        // Real SWF's ListDomains is eventually consistent, so a registration that
                        // succeeded but had not yet propagated would otherwise read as "nothing to
                        // deprecate" — a green step over a leaked domain, and SWF has no delete.
                        bool exists = domainRegistered
                            || await DomainExistsAsync(client, domainName).ConfigureAwait(false);

                        if (!exists)
                        {
                            return "The registration did not land — no domain named "
                                + $"{domainName} exists, so there was nothing to deprecate.";
                        }

                        DeprecateDomainResponse response = await client.DeprecateDomainAsync(
                            new DeprecateDomainRequest { Name = domainName }, CancellationToken.None).ConfigureAwait(false);

                        return $"HTTP {(int)response.HttpStatusCode} — deprecated the domain"
                            + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                    }).ConfigureAwait(false);
            }
        }

        if (deprecateDomainStep is not null)
        {
            yield return deprecateDomainStep;
        }
    }

    /// <summary>
    /// Finds a domain by name, for the cleanup path where <c>RegisterDomain</c>'s response never
    /// arrived. Returns false when no such domain exists, which is proof the registration did not
    /// land.
    /// </summary>
    private static async Task<bool> DomainExistsAsync(IAmazonSimpleWorkflow client, string name)
    {
        ListDomainsResponse response = await client.ListDomainsAsync(
            new ListDomainsRequest { RegistrationStatus = RegistrationStatus.REGISTERED }, CancellationToken.None).ConfigureAwait(false);

        return (response.DomainInfos.Infos ?? []).Any(d => d.Name == name);
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
    /// the emulator does something real SWF would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still deprecates the
        // domain. Catching it here would instead fabricate a "Failed" step for every remaining
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
