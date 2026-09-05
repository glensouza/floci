using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using FlociLab.Core;

namespace FlociLab.Aws.Ssm;

/// <summary>
/// AWS Systems Manager Parameter Store against floci. Ordinary AWSSDK.SimpleSystemsManagement
/// code — the only emulator-aware line in the sample is in <see cref="SsmClientFactory"/>.
/// </summary>
public sealed class SsmDemo(SsmClientFactory factory) : IServiceDemo
{
    private const string InitialValue = "Hello from FlociLab.";
    private const string UpdatedValue = "Updated from FlociLab.";

    public string Provider => CloudProvider.Aws;

    public string Slug => "ssm";

    public string DisplayName => "SSM Parameter Store";

    public string Category => "Configuration";

    public string Route => "/aws/ssm";

    /// <summary>DescribeParameters — one request, no state, and the cheapest call SSM has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonSimpleSystemsManagement client = factory.Create();
            DescribeParametersResponse response = await client.DescribeParametersAsync(new DescribeParametersRequest(), ct).ConfigureAwait(false);
            int count = response.Parameters?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"DescribeParameters returned {count} parameter(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonSimpleSystemsManagement client = factory.Create();

        // Unique per run, so two runs never collide and a leftover parameter from a crashed run
        // never makes the next one fail. Parameter names allow up to 2048 chars of a restricted
        // character set; a path-style name under a private namespace stays well clear of it.
        string name = $"/flocilab/ssm/{Guid.NewGuid():N}";
        bool created = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "DescribeParameters — before",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSSM.DescribeParameters\nclient.DescribeParametersAsync(new DescribeParametersRequest())",
                async () =>
                {
                    DescribeParametersResponse response = await client.DescribeParametersAsync(new DescribeParametersRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Parameters?.Count ?? 0} parameter(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutParameter",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSSM.PutParameter\nclient.PutParameterAsync(new PutParameterRequest {{ Name = \"{name}\", Value = \"{InitialValue}\", Type = ParameterType.String }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the parameter exists and cleanup has to know about it.
                    // Cleanup treats an absent parameter as a no-op, so claiming it early is free.
                    created = true;
                    PutParameterResponse response = await client.PutParameterAsync(
                        new PutParameterRequest { Name = name, Value = InitialValue, Type = ParameterType.String }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — Version: {response.Version}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetParameter",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSSM.GetParameter\nclient.GetParameterAsync(new GetParameterRequest {{ Name = \"{name}\" }})",
                async () =>
                {
                    GetParameterResponse response = await client.GetParameterAsync(
                        new GetParameterRequest { Name = name }, ct).ConfigureAwait(false);

                    // A round-trip that returns something other than what was created did not
                    // round-trip. The lede promises this page shows what floci actually answered,
                    // so a mismatch goes out red rather than a green badge over a broken read.
                    if (response.Parameter?.Value != InitialValue)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — Value was \"{response.Parameter?.Value}\", not the value PutParameter set.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — Value: {response.Parameter.Value}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutParameter — update",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSSM.PutParameter\nclient.PutParameterAsync(new PutParameterRequest {{ Name = \"{name}\", Value = \"{UpdatedValue}\", Type = ParameterType.String, Overwrite = true }})",
                async () =>
                {
                    // Overwrite is required from the second PutParameter on: without it, real SSM
                    // (and floci) answers ParameterAlreadyExists rather than creating a new version.
                    PutParameterResponse response = await client.PutParameterAsync(
                        new PutParameterRequest { Name = name, Value = UpdatedValue, Type = ParameterType.String, Overwrite = true }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — Version: {response.Version}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetParameter — after update",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSSM.GetParameter\nclient.GetParameterAsync(new GetParameterRequest {{ Name = \"{name}\" }})",
                async () =>
                {
                    GetParameterResponse response = await client.GetParameterAsync(
                        new GetParameterRequest { Name = name }, ct).ConfigureAwait(false);

                    // Same rule as the first GetParameter: a read that still shows the old value
                    // means the second PutParameter did not actually create a new current version.
                    if (response.Parameter?.Value != UpdatedValue)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — Value was \"{response.Parameter?.Value}\", not the value PutParameter — update set.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — Value: {response.Parameter.Value}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await this.DeleteParameterAsync(client, name, ct).ConfigureAwait(false) : null;
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
    /// the emulator does something real SSM would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the
        // parameter. Catching it here would instead fabricate a "Failed" step for every remaining
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

    /// <summary>
    /// A parameter that genuinely never got created answers with
    /// <see cref="ParameterNotFoundException"/>, which is a clean run finishing, not a cleanup
    /// failure worth showing in red. The call uses <see cref="CancellationToken.None"/> — a run
    /// that was cancelled still has a parameter to remove.
    /// </summary>
    private async Task<DemoStep> DeleteParameterAsync(IAmazonSimpleSystemsManagement client, string name, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nX-Amz-Target: AmazonSSM.DeleteParameter\nclient.DeleteParameterAsync(new DeleteParameterRequest {{ Name = \"{name}\" }})";

        return await RunStepAsync("DeleteParameter — cleanup", request, async () =>
        {
            try
            {
                DeleteParameterResponse response = await client.DeleteParameterAsync(
                    new DeleteParameterRequest { Name = name }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — removed the parameter"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (ParameterNotFoundException)
            {
                return "Nothing to remove — the parameter was never created.";
            }
        }).ConfigureAwait(false);
    }
}
