using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Pipes;
using Amazon.Pipes.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.EventBridgePipes;

/// <summary>
/// AWS EventBridge Pipes against floci. Ordinary AWSSDK.Pipes code — the only emulator-aware line
/// in the sample is in <see cref="EventBridgePipesClientFactory"/>.
/// </summary>
public sealed class EventBridgePipesDemo(EventBridgePipesClientFactory factory) : IServiceDemo
{
    public string Provider => CloudProvider.Aws;

    public string Slug => "eventbridgepipes";

    public string DisplayName => "EventBridge Pipes";

    public string Category => "Messaging";

    public string Route => "/aws/eventbridgepipes";

    /// <summary>ListPipes — one request, no state, and the cheapest call Pipes has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonPipes client = factory.Create();
            ListPipesResponse response = await client.ListPipesAsync(new ListPipesRequest(), ct).ConfigureAwait(false);
            int count = response.Pipes?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListPipes returned {count} pipe(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonPipes client = factory.Create();

        // Unique per run, so two runs never collide and a leftover pipe from a crashed run never
        // makes the next one fail.
        string suffix = Guid.NewGuid().ToString("N");
        string pipeName = $"flocilab-pipe-{suffix}";

        // A source, a target and a role this sample never creates. floci accepts all three as
        // opaque strings, which is what lets the pipe shape be demonstrated without a second cloud
        // package (constraint 1: a real source and target would need AWSSDK.SQS, and a real role
        // AWSSDK.IdentityManagement).
        //
        // This is an emulator divergence, not a property of Pipes (§14). Real CreatePipe validates
        // at creation time — it resolves the source ARN and assumes the role — so against real AWS
        // (UseEmulator=false, the red "REAL AWS" badge on the page) these ARNs fail the CreatePipe
        // step and the run stops there. The EventBridge sample's fake target ARN is genuinely
        // recorded without validation; that reasoning does not carry over to here.
        string sourceArn = $"arn:aws:sqs:{factory.Region}:000000000000:flocilab-pipes-source-{suffix}";
        string targetArn = $"arn:aws:sqs:{factory.Region}:000000000000:flocilab-pipes-target-{suffix}";
        string roleArn = "arn:aws:iam::000000000000:role/flocilab-pipes-role";

        bool pipeCreated = false;

        DemoStep? deletePipeStep = null;

        try
        {
            yield return await RunStepAsync(
                "CreatePipe",
                $"POST {factory.ServiceUrl}/v1/pipes/{pipeName}\nclient.CreatePipeAsync(new CreatePipeRequest {{ Name = \"{pipeName}\", RoleArn = \"{roleArn}\", Source = \"{sourceArn}\", Target = \"{targetArn}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the pipe exists and cleanup has to know about it.
                    pipeCreated = true;
                    CreatePipeResponse response = await client.CreatePipeAsync(
                        new CreatePipeRequest { Name = pipeName, RoleArn = roleArn, Source = sourceArn, Target = targetArn }, ct).ConfigureAwait(false);

                    // CurrentState is reported, deliberately not asserted. floci answers RUNNING
                    // here and to every transition below, synchronously; real Pipes answers
                    // CREATING and reaches RUNNING later (§14). Asserting either value would make
                    // the step wrong against the other target, and this sample runs against both.
                    return $"HTTP {(int)response.HttpStatusCode} — Arn: {response.Arn}, CurrentState: {response.CurrentState}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribePipe",
                $"GET {factory.ServiceUrl}/v1/pipes/{pipeName}\nclient.DescribePipeAsync(new DescribePipeRequest {{ Name = \"{pipeName}\" }})",
                async () =>
                {
                    DescribePipeResponse response = await client.DescribePipeAsync(
                        new DescribePipeRequest { Name = pipeName }, ct).ConfigureAwait(false);

                    // A pipe that did not round-trip its source and target did not round-trip. The
                    // lede promises this page shows what floci actually answered, so a mismatch
                    // goes out red rather than a green badge over a broken read.
                    if (response.Source != sourceArn || response.Target != targetArn)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — Source: {response.Source}, Target: {response.Target}");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — CurrentState: {response.CurrentState}, Source: {response.Source}, Target: {response.Target}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListPipes",
                $"GET {factory.ServiceUrl}/v1/pipes\nclient.ListPipesAsync(new ListPipesRequest())",
                async () =>
                {
                    ListPipesResponse response = await client.ListPipesAsync(new ListPipesRequest(), ct).ConfigureAwait(false);

                    List<Pipe> pipes = response.Pipes ?? [];

                    // A listing that does not contain the pipe this run provably just created has
                    // not listed it, however many other pipes came back (§14). Returning the bare
                    // count instead would paint an empty listing green — the shape this repo has
                    // now found in every list step it did not assert.
                    if (!pipes.Any(p => p.Name == pipeName))
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — {pipes.Count} pipe(s), none of them {pipeName}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {pipes.Count} pipe(s), including {pipeName}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "StopPipe",
                $"POST {factory.ServiceUrl}/v1/pipes/{pipeName}/stop\nclient.StopPipeAsync(new StopPipeRequest {{ Name = \"{pipeName}\" }})",
                async () =>
                {
                    StopPipeResponse response = await client.StopPipeAsync(
                        new StopPipeRequest { Name = pipeName }, ct).ConfigureAwait(false);

                    // Both states, so the check holds against floci and real AWS alike: floci
                    // answers STOPPED synchronously, real Pipes answers STOPPING and settles later
                    // (§14). What it rules out is the one reading that would be a lie either way —
                    // a 200 still reporting RUNNING, which is a stop that stopped nothing.
                    if (response.CurrentState != PipeState.STOPPED && response.CurrentState != PipeState.STOPPING)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — StopPipe answered CurrentState: {response.CurrentState}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — CurrentState: {response.CurrentState}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "StartPipe",
                $"POST {factory.ServiceUrl}/v1/pipes/{pipeName}/start\nclient.StartPipeAsync(new StartPipeRequest {{ Name = \"{pipeName}\" }})",
                async () =>
                {
                    StartPipeResponse response = await client.StartPipeAsync(
                        new StartPipeRequest { Name = pipeName }, ct).ConfigureAwait(false);

                    // Mirror of StopPipe above: RUNNING on floci, STARTING on real Pipes, and a
                    // 200 still reporting STOPPED is a start that started nothing.
                    if (response.CurrentState != PipeState.RUNNING && response.CurrentState != PipeState.STARTING)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — StartPipe answered CurrentState: {response.CurrentState}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — CurrentState: {response.CurrentState}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            if (pipeCreated)
            {
                deletePipeStep = await RunStepAsync(
                    "DeletePipe — cleanup",
                    $"DELETE {factory.ServiceUrl}/v1/pipes/{pipeName}\nclient.DeletePipeAsync(new DeletePipeRequest {{ Name = \"{pipeName}\" }})",
                    async () =>
                    {
                        DeletePipeResponse response;

                        try
                        {
                            response = await client.DeletePipeAsync(
                                new DeletePipeRequest { Name = pipeName }, CancellationToken.None).ConfigureAwait(false);
                        }
                        // pipeCreated is set before CreatePipe, so a CreatePipe that never landed
                        // still reaches here. Deleting a pipe that does not exist is a 404 —
                        // floci and real Pipes agree, so this is proof nothing was created rather
                        // than a delete that silently removed nothing, which is what makes the
                        // green badge below truthful (§14).
                        catch (NotFoundException)
                        {
                            return "Nothing to remove — the pipe was never created.";
                        }

                        return $"HTTP {(int)response.HttpStatusCode} — removed the pipe"
                            + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                    }).ConfigureAwait(false);
            }
        }

        if (deletePipeStep is not null)
        {
            yield return deletePipeStep;
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
    /// the emulator does something real EventBridge Pipes would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the pipe.
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
}
