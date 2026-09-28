using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.APIGateway;
using Amazon.APIGateway.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.ApiGatewayRest;

/// <summary>
/// Amazon API Gateway (REST APIs) against floci. Ordinary AWSSDK.APIGateway code for every
/// management-plane call; the final step invokes the deployed stage over plain HTTP, which is
/// what a production caller does too — <c>execute-api</c> is never reached through the SDK. The
/// only emulator-aware lines are in <see cref="ApiGatewayRestClientFactory"/>.
/// </summary>
public sealed class ApiGatewayRestDemo(ApiGatewayRestClientFactory factory, IHttpClientFactory httpClientFactory) : IServiceDemo
{
    private const string ResourcePathPart = "probe";

    private const string StageName = "demo";

    private const string ResponseMessage = "hello from FlociLab";

    public string Provider => CloudProvider.Aws;

    public string Slug => "apigatewayrest";

    public string DisplayName => "API Gateway REST";

    public string Category => "API";

    public string Route => "/aws/apigatewayrest";

    /// <summary>GetRestApis — one request, no state, and the cheapest call the service has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonAPIGateway client = factory.Create();
            GetRestApisResponse response = await client.GetRestApisAsync(new GetRestApisRequest(), ct).ConfigureAwait(false);

            // AWSSDK v4 leaves an absent response collection null rather than empty (§14), and an
            // account with no REST APIs is the normal state of a fresh emulator — so an unguarded
            // .Count would throw here and Classify would report a healthy service as Error.
            int count = response.Items?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetRestApis returned {count} API(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonAPIGateway client = factory.Create();

        // Unique per run, so two runs never collide and a leftover API from a crashed run never
        // makes the next one fail.
        string apiName = $"flocilab-apigatewayrest-{Guid.NewGuid():N}";
        bool created = false;
        string? restApiId = null;

        DemoStep? cleanup;

        try
        {
            string? rootResourceId = null;
            string? resourceId = null;

            // A do/while(false), so the early exits below are `break` rather than trying to
            // thread a chain of booleans through six sequential yields. One real fault should
            // render as one red step, not six — a stopped emulator that fails CreateRestApi
            // should not also fail every step that depends on the resource it never created.
            // A plain `break` here (not `yield break`) so the finally below still runs and the
            // cleanup step below still gets yielded regardless of where the sequence stops.
            do
            {
                yield return await RunStepAsync(
                    "CreateRestApi",
                    $"POST {factory.ServiceUrl}/restapis\napigateway.CreateRestApiAsync(new CreateRestApiRequest {{ Name = \"{apiName}\" }})",
                    async () =>
                    {
                        // Set before the call, not after: if the request lands but the response
                        // does not come back, the API exists and cleanup has to know about it.
                        // Cleanup treats an absent API as a no-op, so claiming it early is free.
                        created = true;
                        CreateRestApiResponse response = await client.CreateRestApiAsync(
                            new CreateRestApiRequest { Name = apiName }, ct).ConfigureAwait(false);
                        restApiId = response.Id;
                        rootResourceId = response.RootResourceId;

                        return $"HTTP {(int)response.HttpStatusCode} — id: {response.Id}, rootResourceId: {response.RootResourceId}";
                    }).ConfigureAwait(false);

                if (restApiId is null || rootResourceId is null)
                {
                    break;
                }

                yield return await RunStepAsync(
                    "CreateResource",
                    $"POST {factory.ServiceUrl}/restapis/{restApiId}/resources/{rootResourceId}\napigateway.CreateResourceAsync(new CreateResourceRequest {{ RestApiId = \"{restApiId}\", ParentId = \"{rootResourceId}\", PathPart = \"{ResourcePathPart}\" }})",
                    async () =>
                    {
                        CreateResourceResponse response = await client.CreateResourceAsync(
                            new CreateResourceRequest { RestApiId = restApiId, ParentId = rootResourceId, PathPart = ResourcePathPart }, ct).ConfigureAwait(false);
                        resourceId = response.Id;

                        return $"HTTP {(int)response.HttpStatusCode} — id: {response.Id}, path: {response.Path}";
                    }).ConfigureAwait(false);

                if (resourceId is null)
                {
                    break;
                }

                bool methodWired = false;

                yield return await RunStepAsync(
                    "PutMethod + PutIntegration (MOCK)",
                    $"PUT {factory.ServiceUrl}/restapis/{restApiId}/resources/{resourceId}/methods/GET\napigateway.PutMethodAsync(...)\nPUT .../integration\napigateway.PutIntegrationAsync(new PutIntegrationRequest {{ Type = MOCK }})",
                    async () =>
                    {
                        PutMethodResponse methodResponse = await client.PutMethodAsync(
                            new PutMethodRequest { RestApiId = restApiId, ResourceId = resourceId, HttpMethod = "GET", AuthorizationType = "NONE" }, ct).ConfigureAwait(false);

                        PutIntegrationResponse integrationResponse = await client.PutIntegrationAsync(
                            new PutIntegrationRequest
                            {
                                RestApiId = restApiId,
                                ResourceId = resourceId,
                                HttpMethod = "GET",
                                Type = IntegrationType.MOCK,
                                RequestTemplates = new Dictionary<string, string> { ["application/json"] = "{\"statusCode\": 200}" },
                            }, ct).ConfigureAwait(false);

                        methodWired = true;

                        return $"HTTP {(int)methodResponse.HttpStatusCode} — method GET wired to a MOCK integration (HTTP {(int)integrationResponse.HttpStatusCode})";
                    }).ConfigureAwait(false);

                if (!methodWired)
                {
                    break;
                }

                bool responsesWired = false;

                yield return await RunStepAsync(
                    "PutMethodResponse + PutIntegrationResponse",
                    $"PUT {factory.ServiceUrl}/restapis/{restApiId}/resources/{resourceId}/methods/GET/responses/200\napigateway.PutMethodResponseAsync(...)\nPUT .../integration/responses/200\napigateway.PutIntegrationResponseAsync(new PutIntegrationResponseRequest {{ ResponseTemplates = [ \"{{\\\"message\\\": \\\"{ResponseMessage}\\\"}}\" ] }})",
                    async () =>
                    {
                        PutMethodResponseResponse methodResponse = await client.PutMethodResponseAsync(
                            new PutMethodResponseRequest { RestApiId = restApiId, ResourceId = resourceId, HttpMethod = "GET", StatusCode = "200" }, ct).ConfigureAwait(false);

                        PutIntegrationResponseResponse integrationResponse = await client.PutIntegrationResponseAsync(
                            new PutIntegrationResponseRequest
                            {
                                RestApiId = restApiId,
                                ResourceId = resourceId,
                                HttpMethod = "GET",
                                StatusCode = "200",
                                ResponseTemplates = new Dictionary<string, string> { ["application/json"] = $"{{\"message\": \"{ResponseMessage}\"}}" },
                            }, ct).ConfigureAwait(false);

                        responsesWired = true;

                        return $"HTTP {(int)methodResponse.HttpStatusCode} — 200 response wired on both method and integration (HTTP {(int)integrationResponse.HttpStatusCode})";
                    }).ConfigureAwait(false);

                if (!responsesWired)
                {
                    break;
                }

                bool deployed = false;

                yield return await RunStepAsync(
                    "CreateDeployment",
                    $"POST {factory.ServiceUrl}/restapis/{restApiId}/deployments\napigateway.CreateDeploymentAsync(new CreateDeploymentRequest {{ RestApiId = \"{restApiId}\", StageName = \"{StageName}\" }})",
                    async () =>
                    {
                        CreateDeploymentResponse response = await client.CreateDeploymentAsync(
                            new CreateDeploymentRequest { RestApiId = restApiId, StageName = StageName }, ct).ConfigureAwait(false);
                        deployed = true;

                        return $"HTTP {(int)response.HttpStatusCode} — deployment id: {response.Id}, stage: {StageName}";
                    }).ConfigureAwait(false);

                if (!deployed)
                {
                    break;
                }

                // The invoke step is plain HTTP, not an SDK call — production code reaches a
                // deployed stage through its execute-api URL, never through AWSSDK.APIGateway
                // (see ApiGatewayRestClientFactory.InvokeUrl). This is what actually proves the
                // round trip: everything above only proves the API was *configured*, not that it
                // answers requests.
                string invokeUrl = factory.InvokeUrl(restApiId, StageName, ResourcePathPart);

                yield return await RunStepAsync(
                    "Invoke deployed stage",
                    $"GET {invokeUrl}",
                    async () =>
                    {
                        using HttpClient http = httpClientFactory.CreateClient();
                        using HttpResponseMessage response = await http.GetAsync(invokeUrl, ct).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        // The round trip is only proven by the mock response this run wired up
                        // actually coming back, not by the call merely returning 200.
                        if (!body.Contains(ResponseMessage, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} — body did not contain the expected mock response: {body}");
                        }

                        return $"HTTP {(int)response.StatusCode} — {body}";
                    }).ConfigureAwait(false);
            }
            while (false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await this.DeleteRestApiAsync(client, restApiId, apiName, ct).ConfigureAwait(false) : null;
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
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
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

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Deletes by the id captured from CreateRestApi's response when there is one, and otherwise
    /// finds the API by this run's unique name — the same fallback SQS's cleanup makes with
    /// GetQueueUrl. A CreateRestApi whose request landed but whose response was lost still left an
    /// API behind, and GetRestApis lists it. The calls use <see cref="CancellationToken.None"/> — a
    /// run that was cancelled still has an API to remove.
    /// </summary>
    private async Task<DemoStep> DeleteRestApiAsync(IAmazonAPIGateway client, string? restApiId, string apiName, CancellationToken ct)
    {
        string request = restApiId is not null
            ? $"DELETE {factory.ServiceUrl}/restapis/{restApiId}\napigateway.DeleteRestApiAsync(new DeleteRestApiRequest {{ RestApiId = \"{restApiId}\" }})"
            : $"GET {factory.ServiceUrl}/restapis\napigateway.GetRestApisAsync() — find \"{apiName}\"\nDELETE {factory.ServiceUrl}/restapis/{{id}}\napigateway.DeleteRestApiAsync(...)";

        return await RunStepAsync("DeleteRestApi — cleanup", request, async () =>
        {
            string? id = restApiId ?? await FindRestApiIdAsync(client, apiName).ConfigureAwait(false);

            if (id is null)
            {
                return $"Nothing to remove — no API named \"{apiName}\" exists.";
            }

            DeleteRestApiResponse response = await client.DeleteRestApiAsync(
                new DeleteRestApiRequest { RestApiId = id }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — removed the API"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    private static async Task<string?> FindRestApiIdAsync(IAmazonAPIGateway client, string apiName)
    {
        string? position = null;

        do
        {
            GetRestApisResponse page = await client.GetRestApisAsync(
                new GetRestApisRequest { Position = position, Limit = 500 }, CancellationToken.None).ConfigureAwait(false);

            // Items is null rather than empty on an account with no APIs (AWSSDK v4, §14).
            RestApi? match = page.Items?.FirstOrDefault(api => api.Name == apiName);

            if (match is not null)
            {
                return match.Id;
            }

            position = page.Position;
        }
        while (!string.IsNullOrEmpty(position));

        return null;
    }
}
