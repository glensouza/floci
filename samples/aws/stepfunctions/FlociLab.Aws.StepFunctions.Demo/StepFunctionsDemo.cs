using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using FlociLab.Core;

namespace FlociLab.Aws.StepFunctions;

/// <summary>
/// AWS Step Functions against floci. Ordinary AWSSDK.StepFunctions code — the only emulator-aware
/// line in the sample is in <see cref="StepFunctionsClientFactory"/>.
/// </summary>
public sealed class StepFunctionsDemo(StepFunctionsClientFactory factory) : IServiceDemo
{
    // A single Pass state that echoes a fixed result. floci actually executes this — unlike
    // EventBridge Scheduler's target, there is no second service to invoke, so the state machine
    // needs no ARN it never dereferences and no second cloud package (constraint 1).
    private const string StateMachineDefinition = """{"Comment":"flocilab demo","StartAt":"Done","States":{"Done":{"Type":"Pass","Result":"ok","End":true}}}""";

    // Only ever used against real AWS — floci answers SUCCEEDED on the first poll. Ten attempts
    // at 500 ms is a generous ceiling for a single Pass state, and short enough that a viewer
    // watching the page never waits on a workflow that is not going to finish.
    private const int ExecutionPollAttempts = 10;

    private static readonly TimeSpan ExecutionPollDelay = TimeSpan.FromMilliseconds(500);

    public string Provider => CloudProvider.Aws;

    public string Slug => "stepfunctions";

    public string DisplayName => "Step Functions";

    public string Category => "Workflows";

    public string Route => "/aws/stepfunctions";

    /// <summary>ListStateMachines — one request, no state, and the cheapest call Step Functions has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonStepFunctions client = factory.Create();
            ListStateMachinesResponse response = await client.ListStateMachinesAsync(new ListStateMachinesRequest(), ct).ConfigureAwait(false);
            int count = response.StateMachines?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListStateMachines returned {count} state machine(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonStepFunctions client = factory.Create();

        // Unique per run, so two runs never collide and a leftover state machine from a crashed
        // run never makes the next one fail.
        string suffix = Guid.NewGuid().ToString("N");
        string stateMachineName = $"flocilab-stepfunctions-{suffix}";
        string executionName = $"flocilab-execution-{suffix}";

        // A well-formed ARN for a role that does not exist. floci checks the ARN's *shape* — a
        // malformed one comes back as InvalidArn — but not whether the role exists or is
        // assumable, so this create succeeds where real Step Functions would reject it. Probed
        // against floci 1.7.0, 2026-09-06; see docs/BLAZOR-PLAN.md §14.
        string roleArn = "arn:aws:iam::000000000000:role/flocilab-stepfunctions-role";

        string? stateMachineArn = null;
        bool createAttempted = false;
        DemoStep? deleteStateMachineStep = null;

        try
        {
            yield return await RunStepAsync(
                "CreateStateMachine",
                $"POST {factory.ServiceUrl}/\nclient.CreateStateMachineAsync(new CreateStateMachineRequest {{ Name = \"{stateMachineName}\", Definition = \"{StateMachineDefinition}\", RoleArn = \"{roleArn}\" }})",
                async () =>
                {
                    // Set before the call, not after (docs/BLAZOR-PLAN.md §14): cleanup is gated
                    // on "the request was issued", never on "the response said yes". If floci
                    // creates the state machine and the response is then lost — a dropped
                    // connection, or the page cancelling this token because the viewer navigated
                    // away — the state machine exists and cleanup has to know. Unlike Scheduler's
                    // deterministic name, DeleteStateMachine needs an ARN, so the finally below
                    // resolves it by name rather than assuming this response arrived.
                    createAttempted = true;

                    CreateStateMachineResponse response = await client.CreateStateMachineAsync(
                        new CreateStateMachineRequest
                        {
                            Name = stateMachineName,
                            Definition = StateMachineDefinition,
                            RoleArn = roleArn,
                        }, ct).ConfigureAwait(false);

                    stateMachineArn = response.StateMachineArn;

                    return $"HTTP {(int)response.HttpStatusCode} — StateMachineArn: {response.StateMachineArn}";
                }).ConfigureAwait(false);

            // One real fault should render as one red step, not five. Without this, a failed
            // create runs DescribeStateMachine, StartExecution and ListExecutions against a null
            // ARN and paints four more failures carrying SDK-internal or empty-ARN messages. The
            // finally still runs, so anything the create left behind is still cleaned up.
            if (stateMachineArn is null)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "DescribeStateMachine",
                $"POST {factory.ServiceUrl}/\nclient.DescribeStateMachineAsync(new DescribeStateMachineRequest {{ StateMachineArn = \"{stateMachineArn}\" }})",
                async () =>
                {
                    DescribeStateMachineResponse response = await client.DescribeStateMachineAsync(
                        new DescribeStateMachineRequest { StateMachineArn = stateMachineArn }, ct).ConfigureAwait(false);

                    // A state machine that did not round-trip its own definition did not
                    // round-trip. The lede promises this page shows what floci actually answered,
                    // so a mismatch goes out red rather than a green badge over a broken read.
                    if (response.Definition != StateMachineDefinition)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — Definition did not round-trip: {response.Definition}");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — Status: {response.Status}, Name: {response.Name}";
                }).ConfigureAwait(false);

            string? executionArn = null;

            yield return await RunStepAsync(
                "StartExecution",
                $"POST {factory.ServiceUrl}/\nclient.StartExecutionAsync(new StartExecutionRequest {{ StateMachineArn = \"{stateMachineArn}\", Name = \"{executionName}\", Input = \"{{}}\" }})",
                async () =>
                {
                    StartExecutionResponse response = await client.StartExecutionAsync(
                        new StartExecutionRequest
                        {
                            StateMachineArn = stateMachineArn,
                            Name = executionName,
                            Input = "{}",
                        }, ct).ConfigureAwait(false);

                    executionArn = response.ExecutionArn;

                    return $"HTTP {(int)response.HttpStatusCode} — ExecutionArn: {response.ExecutionArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeExecution",
                $"POST {factory.ServiceUrl}/\nclient.DescribeExecutionAsync(new DescribeExecutionRequest {{ ExecutionArn = \"{executionArn}\" }})",
                async () =>
                {
                    // floci runs the single Pass state synchronously, so against the emulator
                    // this loop reads RUNNING never and exits on its first pass. The loop is here
                    // for the other target: real Step Functions' StartExecution is asynchronous
                    // and returns while the execution is still RUNNING, so asserting SUCCEEDED
                    // straight off the first Describe would paint this step red on a perfectly
                    // healthy AWS account — and the page reaches real AWS whenever UseEmulator is
                    // false. See docs/BLAZOR-PLAN.md §14.
                    DescribeExecutionResponse response;

                    for (int attempt = 0; ; attempt++)
                    {
                        response = await client.DescribeExecutionAsync(
                            new DescribeExecutionRequest { ExecutionArn = executionArn }, ct).ConfigureAwait(false);

                        if (response.Status != ExecutionStatus.RUNNING)
                        {
                            break;
                        }

                        // An exhausted cap is a failure, never a success carrying the last-seen
                        // status (§14) — a one-state workflow that has not finished in this long
                        // is not one this page should badge green.
                        if (attempt == ExecutionPollAttempts - 1)
                        {
                            throw new InvalidOperationException(
                                $"Still RUNNING after {ExecutionPollAttempts} polls over "
                                + $"{ExecutionPollAttempts * ExecutionPollDelay.TotalSeconds:0.#}s.");
                        }

                        await Task.Delay(ExecutionPollDelay, ct).ConfigureAwait(false);
                    }

                    if (response.Status != ExecutionStatus.SUCCEEDED)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — Status: {response.Status}, expected SUCCEEDED.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — Status: {response.Status}, Output: {response.Output}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListExecutions",
                $"POST {factory.ServiceUrl}/\nclient.ListExecutionsAsync(new ListExecutionsRequest {{ StateMachineArn = \"{stateMachineArn}\" }})",
                async () =>
                {
                    ListExecutionsResponse response = await client.ListExecutionsAsync(
                        new ListExecutionsRequest { StateMachineArn = stateMachineArn }, ct).ConfigureAwait(false);

                    List<ExecutionListItem> executions = response.Executions ?? [];

                    // A listing that does not contain the execution this run provably just
                    // started has not listed it, however many other executions came back.
                    if (!executions.Any(e => e.ExecutionArn == executionArn))
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — {executions.Count} execution(s), none of them {executionArn}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {executions.Count} execution(s), including this run's";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped
            // enumerating, so a re-run always starts from a clean account. The step it produces
            // is yielded below — an iterator may not yield from inside a finally.
            if (createAttempted)
            {
                deleteStateMachineStep = await RunStepAsync(
                    "DeleteStateMachine — cleanup",
                    $"POST {factory.ServiceUrl}/\nclient.DeleteStateMachineAsync(new DeleteStateMachineRequest {{ StateMachineArn = \"{stateMachineArn ?? stateMachineName}\" }})",
                    async () =>
                    {
                        // CancellationToken.None throughout: this runs precisely when the caller
                        // has given up, and a cleanup that honoured ct would never delete anything
                        // on the cancelled runs that need it most.

                        // The response may have been lost even though the create landed, so ask
                        // the server what exists rather than guessing an ARN (§14, third
                        // corollary). A listing that does not contain the name is proof nothing
                        // was created — a truthful green step, not a silent skip.
                        string? arn = stateMachineArn ?? await ResolveByNameAsync(client, stateMachineName).ConfigureAwait(false);

                        if (arn is null)
                        {
                            return "The create did not land — no state machine named "
                                + $"{stateMachineName} exists, so there was nothing to remove.";
                        }

                        DeleteStateMachineResponse response = await client.DeleteStateMachineAsync(
                            new DeleteStateMachineRequest { StateMachineArn = arn }, CancellationToken.None).ConfigureAwait(false);

                        return $"HTTP {(int)response.HttpStatusCode} — removed the state machine"
                            + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                    }).ConfigureAwait(false);
            }
        }

        if (deleteStateMachineStep is not null)
        {
            yield return deleteStateMachineStep;
        }
    }

    /// <summary>
    /// Finds a state machine by name, for the cleanup path where <c>CreateStateMachine</c>'s
    /// response never arrived. Returns null when no such state machine exists, which is proof the
    /// create did not land.
    /// </summary>
    private static async Task<string?> ResolveByNameAsync(IAmazonStepFunctions client, string name)
    {
        ListStateMachinesResponse response = await client.ListStateMachinesAsync(
            new ListStateMachinesRequest(), CancellationToken.None).ConfigureAwait(false);

        return (response.StateMachines ?? []).FirstOrDefault(s => s.Name == name)?.StateMachineArn;
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
    /// the emulator does something real Step Functions would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the state
        // machine. Catching it here would instead fabricate a "Failed" step for every remaining
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
