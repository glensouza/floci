using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.ApiGatewayV2;
using Amazon.ApiGatewayV2.Model;
using Amazon.Runtime;
using FlociLab.Core;
using ProtocolType = Amazon.ApiGatewayV2.ProtocolType;

namespace FlociLab.Aws.ApiGatewayV2;

/// <summary>
/// Amazon API Gateway v2 (HTTP APIs) against floci. Ordinary AWSSDK.ApiGatewayV2 code for every
/// management-plane call; the final step invokes the deployed stage over plain HTTP, which is
/// what a production caller does too — <c>execute-api</c> is never reached through the SDK. The
/// only emulator-aware lines are in <see cref="ApiGatewayV2ClientFactory"/>. The WebSocket half
/// of the service shares this package and this management plane but needs a socket client to
/// prove anything, so this sample covers the HTTP protocol only.
/// </summary>
public sealed class ApiGatewayV2Demo(ApiGatewayV2ClientFactory factory, IHttpClientFactory httpClientFactory) : IServiceDemo
{
    private const string RoutePath = "probe";

    private const string StageName = "demo";

    // Real API Gateway's auto-deploy is asynchronous: CreateStage returns before the stage serves
    // traffic, so the first request after it can 404 (HTTP) or refuse the handshake (WebSocket) on
    // a healthy account. floci deploys synchronously, so a green test suite does not rule it out —
    // the same shape as the CloudWatch read-back polls (§14). The first attempt always hits floci.
    internal static readonly TimeSpan DeployPollBudget = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan DeployPollDelay = TimeSpan.FromSeconds(1);

    public string Provider => CloudProvider.Aws;

    public string Slug => "apigatewayv2";

    public string DisplayName => "API Gateway v2";

    public string Category => "API";

    public string Route => "/aws/apigatewayv2";

    /// <summary>GetApis — one request, no state, and the cheapest call the service has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonApiGatewayV2 client = factory.Create();
            GetApisResponse response = await client.GetApisAsync(new GetApisRequest(), ct).ConfigureAwait(false);

            // AWSSDK v4 leaves an absent response collection null rather than empty (§14), and an
            // account with no APIs is the normal state of a fresh emulator — so an unguarded
            // .Count would throw here and Classify would report a healthy service as Error.
            int count = response.Items?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetApis returned {count} API(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonApiGatewayV2 client = factory.Create();

        // Unique per run, so two runs never collide and a leftover API from a crashed run never
        // makes the next one fail.
        string apiName = $"flocilab-apigatewayv2-{Guid.NewGuid():N}";
        bool created = false;
        string? apiId = null;

        DemoStep? cleanup;

        try
        {
            string? integrationId = null;

            // A do/while(false), so the early exits below are `break` rather than trying to
            // thread a chain of booleans through five sequential yields. One real fault should
            // render as one red step, not five — a stopped emulator that fails CreateApi should
            // not also fail every step that depends on the API it never created. A plain `break`
            // here (not `yield break`) so the finally below still runs and the cleanup step still
            // gets yielded regardless of where the sequence stops.
            do
            {
                yield return await RunStepAsync(
                    "CreateApi",
                    $"POST {factory.ServiceUrl}/v2/apis\napigatewayv2.CreateApiAsync(new CreateApiRequest {{ Name = \"{apiName}\", ProtocolType = HTTP }})",
                    async () =>
                    {
                        // Set before the call, not after: if the request lands but the response
                        // does not come back, the API exists and cleanup has to know about it.
                        // Cleanup treats an absent API as a no-op, so claiming it early is free.
                        created = true;
                        CreateApiResponse response = await client.CreateApiAsync(
                            new CreateApiRequest { Name = apiName, ProtocolType = ProtocolType.HTTP }, ct).ConfigureAwait(false);
                        apiId = response.ApiId;

                        return $"HTTP {(int)response.HttpStatusCode} — apiId: {response.ApiId}, endpoint: {response.ApiEndpoint}";
                    }).ConfigureAwait(false);

                if (apiId is null)
                {
                    break;
                }

                yield return await RunStepAsync(
                    "CreateIntegration (HTTP_PROXY)",
                    $"POST {factory.ServiceUrl}/v2/apis/{apiId}/integrations\napigatewayv2.CreateIntegrationAsync(new CreateIntegrationRequest {{ IntegrationType = HTTP_PROXY, IntegrationMethod = \"GET\", IntegrationUri = \"{factory.IntegrationUri}\", PayloadFormatVersion = \"1.0\" }})",
                    async () =>
                    {
                        CreateIntegrationResponse response = await client.CreateIntegrationAsync(
                            new CreateIntegrationRequest
                            {
                                ApiId = apiId,
                                IntegrationType = IntegrationType.HTTP_PROXY,
                                IntegrationMethod = "GET",
                                IntegrationUri = factory.IntegrationUri,
                                PayloadFormatVersion = "1.0",
                            }, ct).ConfigureAwait(false);
                        integrationId = response.IntegrationId;

                        return $"HTTP {(int)response.HttpStatusCode} — integrationId: {response.IntegrationId}, uri: {response.IntegrationUri}";
                    }).ConfigureAwait(false);

                if (integrationId is null)
                {
                    break;
                }

                bool routed = false;

                yield return await RunStepAsync(
                    "CreateRoute",
                    $"POST {factory.ServiceUrl}/v2/apis/{apiId}/routes\napigatewayv2.CreateRouteAsync(new CreateRouteRequest {{ RouteKey = \"GET /{RoutePath}\", Target = \"integrations/{integrationId}\" }})",
                    async () =>
                    {
                        CreateRouteResponse response = await client.CreateRouteAsync(
                            new CreateRouteRequest { ApiId = apiId, RouteKey = $"GET /{RoutePath}", Target = $"integrations/{integrationId}" }, ct).ConfigureAwait(false);
                        routed = true;

                        return $"HTTP {(int)response.HttpStatusCode} — routeId: {response.RouteId}, routeKey: {response.RouteKey}";
                    }).ConfigureAwait(false);

                if (!routed)
                {
                    break;
                }

                bool staged = false;

                yield return await RunStepAsync(
                    "CreateStage (auto-deploy)",
                    $"POST {factory.ServiceUrl}/v2/apis/{apiId}/stages\napigatewayv2.CreateStageAsync(new CreateStageRequest {{ StageName = \"{StageName}\", AutoDeploy = true }})",
                    async () =>
                    {
                        CreateStageResponse response = await client.CreateStageAsync(
                            new CreateStageRequest { ApiId = apiId, StageName = StageName, AutoDeploy = true }, ct).ConfigureAwait(false);
                        staged = true;

                        return $"HTTP {(int)response.HttpStatusCode} — stage: {response.StageName}, autoDeploy: {response.AutoDeploy}";
                    }).ConfigureAwait(false);

                if (!staged)
                {
                    break;
                }

                // The invoke step is plain HTTP, not an SDK call — production code reaches a
                // deployed stage through its execute-api URL, never through AWSSDK.ApiGatewayV2
                // (see ApiGatewayV2ClientFactory.InvokeUrl). This is what actually proves the
                // round trip: everything above only proves the API was *configured*, not that it
                // answers requests.
                string invokeUrl = factory.InvokeUrl(apiId, StageName, RoutePath);

                yield return await RunStepAsync(
                    "Invoke deployed stage",
                    $"GET {invokeUrl}",
                    async () =>
                    {
                        using HttpClient http = httpClientFactory.CreateClient();
                        long deadline = Stopwatch.GetTimestamp() + (long)(DeployPollBudget.TotalSeconds * Stopwatch.Frequency);

                        while (true)
                        {
                            using HttpResponseMessage response = await http.GetAsync(invokeUrl, ct).ConfigureAwait(false);
                            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                            // The round trip is only proven by the upstream's own body coming back
                            // through the proxy, not by the call merely returning 200.
                            if (body.Contains(factory.IntegrationMarker, StringComparison.Ordinal))
                            {
                                return $"HTTP {(int)response.StatusCode} — proxied {body.Length} chars from {factory.IntegrationUri}";
                            }

                            // An exhausted budget is a failure carrying the last answer, never a
                            // success carrying whatever that answer happened to be.
                            if (Stopwatch.GetTimestamp() >= deadline)
                            {
                                throw new InvalidOperationException($"HTTP {(int)response.StatusCode} — body still did not contain the upstream's response after {DeployPollBudget.TotalSeconds:0} s: {body}");
                            }

                            await Task.Delay(DeployPollDelay, ct).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
            }
            while (false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await DeleteApiAsync(factory.ServiceUrl, client, apiId, apiName).ConfigureAwait(false) : null;
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
    /// the emulator does something real API Gateway would not.
    /// </summary>
    internal static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the API.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    internal static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Deletes by the id captured from CreateApi's response when there is one, and otherwise
    /// finds the API by this run's unique name — the same fallback SQS's cleanup makes with
    /// GetQueueUrl. A CreateApi whose request landed but whose response was lost still left an
    /// API behind, and GetApis lists it. The calls use <see cref="CancellationToken.None"/> — a
    /// run that was cancelled still has an API to remove. Deleting the API takes its integration,
    /// route and stage with it, so there is nothing else to unwind.
    /// </summary>
    internal static async Task<DemoStep> DeleteApiAsync(string serviceUrl, IAmazonApiGatewayV2 client, string? apiId, string apiName)
    {
        string request = apiId is not null
            ? $"DELETE {serviceUrl}/v2/apis/{apiId}\napigatewayv2.DeleteApiAsync(new DeleteApiRequest {{ ApiId = \"{apiId}\" }})"
            : $"GET {serviceUrl}/v2/apis\napigatewayv2.GetApisAsync() — find \"{apiName}\"\nDELETE {serviceUrl}/v2/apis/{{id}}\napigatewayv2.DeleteApiAsync(...)";

        return await RunStepAsync("DeleteApi — cleanup", request, async () =>
        {
            string? id = apiId ?? await FindApiIdAsync(client, apiName).ConfigureAwait(false);

            if (id is null)
            {
                return $"Nothing to remove — no API named \"{apiName}\" exists.";
            }

            DeleteApiResponse response = await client.DeleteApiAsync(
                new DeleteApiRequest { ApiId = id }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — removed the API";
        }).ConfigureAwait(false);
    }

    private static async Task<string?> FindApiIdAsync(IAmazonApiGatewayV2 client, string apiName)
    {
        string? token = null;

        do
        {
            GetApisResponse page = await client.GetApisAsync(
                new GetApisRequest { NextToken = token, MaxResults = "500" }, CancellationToken.None).ConfigureAwait(false);

            // Items is null rather than empty on an account with no APIs (AWSSDK v4, §14).
            Api? match = page.Items?.FirstOrDefault(api => api.Name == apiName);

            if (match is not null)
            {
                return match.ApiId;
            }

            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return null;
    }
}
