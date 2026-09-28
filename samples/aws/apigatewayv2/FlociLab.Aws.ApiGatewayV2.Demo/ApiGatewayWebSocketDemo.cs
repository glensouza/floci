using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.ApiGatewayV2;
using Amazon.ApiGatewayV2.Model;
using FlociLab.Core;
using ProtocolType = Amazon.ApiGatewayV2.ProtocolType;

namespace FlociLab.Aws.ApiGatewayV2;

/// <summary>
/// API Gateway v2 WebSocket APIs against floci. The control plane is AWSSDK.ApiGatewayV2; the
/// part that makes a WebSocket API a WebSocket API — a backend pushing to, inspecting and
/// disconnecting a live client — is the <c>@connections</c> API in AWSSDK.ApiGatewayManagementApi.
/// The client end of the socket is the BCL's <see cref="ClientWebSocket"/>, exactly as a .NET
/// caller of the real service would write it. The API carries one MOCK-backed route that the run
/// never calls: real API Gateway refuses to deploy a WebSocket API with no routes, and the run
/// sets out to prove the socket and the management calls rather than a backend's behaviour.
/// </summary>
public sealed class ApiGatewayWebSocketDemo(ApiGatewayV2ClientFactory factory) : IServiceDemo
{
    private const string StageName = "demo";

    private const string PushMessage = "pushed by FlociLab";

    // The one route the API carries. The client sends "whoami", which it deliberately never matches.
    private const string RouteKey = "ping";

    // Generous for a local emulator, short enough that a stalled run says so instead of hanging.
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    public string Provider => CloudProvider.Aws;

    public string Slug => "apigatewayv2websocket";

    public string DisplayName => "API Gateway v2 (WebSocket)";

    public string Category => "API";

    public string Route => "/aws/apigatewayv2websocket";

    /// <summary>GetApis — same cheapest-call probe as the HTTP demo; both share one control plane.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonApiGatewayV2 client = factory.Create();
            GetApisResponse response = await client.GetApisAsync(new GetApisRequest(), ct).ConfigureAwait(false);

            // AWSSDK v4 leaves an absent response collection null rather than empty (§14).
            int count = response.Items?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetApis returned {count} API(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApiGatewayV2Demo.Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonApiGatewayV2 client = factory.Create();

        // Not a using: a ClientWebSocket whose handshake failed cannot be reused, so the connect
        // step may replace it while the stage finishes deploying. Disposed in the finally.
        ClientWebSocket? socket = null;

        // Unique per run, so two runs never collide and a leftover API from a crashed run never
        // makes the next one fail.
        string apiName = $"flocilab-apigatewayws-{Guid.NewGuid():N}";
        bool created = false;
        string? apiId = null;

        DemoStep? cleanup;

        try
        {
            // A do/while(false) for the same reason as the HTTP demo: one real fault renders as
            // one red step, and a plain `break` still lets the finally and the cleanup step run.
            do
            {
                yield return await ApiGatewayV2Demo.RunStepAsync(
                    "CreateApi (WEBSOCKET)",
                    $"POST {factory.ServiceUrl}/v2/apis\napigatewayv2.CreateApiAsync(new CreateApiRequest {{ Name = \"{apiName}\", ProtocolType = WEBSOCKET, RouteSelectionExpression = \"$request.body.action\" }})",
                    async () =>
                    {
                        // Set before the call, not after: a request that landed without its
                        // response still left an API behind, and cleanup treats absent as a no-op.
                        created = true;
                        CreateApiResponse response = await client.CreateApiAsync(
                            new CreateApiRequest { Name = apiName, ProtocolType = ProtocolType.WEBSOCKET, RouteSelectionExpression = "$request.body.action" }, ct).ConfigureAwait(false);
                        apiId = response.ApiId;

                        return $"HTTP {(int)response.HttpStatusCode} — apiId: {response.ApiId}, endpoint: {response.ApiEndpoint}";
                    }).ConfigureAwait(false);

                if (apiId is null)
                {
                    break;
                }

                string? integrationId = null;

                // Real API Gateway rejects deploying a WebSocket API with no routes ("At least one
                // route is required before deploying the Api"), and a route needs a target. floci
                // accepts the empty API, so only real AWS would catch the omission. MOCK is the
                // cheapest valid target; nothing ever invokes it.
                yield return await ApiGatewayV2Demo.RunStepAsync(
                    "CreateIntegration (MOCK)",
                    $"POST {factory.ServiceUrl}/v2/apis/{apiId}/integrations\napigatewayv2.CreateIntegrationAsync(new CreateIntegrationRequest {{ IntegrationType = MOCK }})",
                    async () =>
                    {
                        CreateIntegrationResponse response = await client.CreateIntegrationAsync(
                            new CreateIntegrationRequest { ApiId = apiId, IntegrationType = IntegrationType.MOCK }, ct).ConfigureAwait(false);
                        integrationId = response.IntegrationId;

                        return $"HTTP {(int)response.HttpStatusCode} — integrationId: {response.IntegrationId}";
                    }).ConfigureAwait(false);

                if (integrationId is null)
                {
                    break;
                }

                bool routed = false;

                yield return await ApiGatewayV2Demo.RunStepAsync(
                    "CreateRoute",
                    $"POST {factory.ServiceUrl}/v2/apis/{apiId}/routes\napigatewayv2.CreateRouteAsync(new CreateRouteRequest {{ RouteKey = \"{RouteKey}\", Target = \"integrations/{integrationId}\" }})",
                    async () =>
                    {
                        CreateRouteResponse response = await client.CreateRouteAsync(
                            new CreateRouteRequest { ApiId = apiId, RouteKey = RouteKey, Target = $"integrations/{integrationId}" }, ct).ConfigureAwait(false);
                        routed = true;

                        return $"HTTP {(int)response.HttpStatusCode} — routeId: {response.RouteId}, routeKey: {response.RouteKey}";
                    }).ConfigureAwait(false);

                if (!routed)
                {
                    break;
                }

                bool staged = false;

                yield return await ApiGatewayV2Demo.RunStepAsync(
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

                string webSocketUrl = factory.WebSocketUrl(apiId, StageName);
                string? connectionId = null;

                // There is no API call that returns a connection's id — a real backend learns it
                // from the $connect event's requestContext. This API has no $connect route to
                // read it from, so the client sends a frame no route matches ("whoami", not
                // "ping"), and API Gateway answers an unmatched frame with an error carrying the
                // connectionId. The message text differs (real AWS says "Forbidden", floci "No
                // route found"); only the connectionId is read, so the run depends on neither.
                yield return await ApiGatewayV2Demo.RunStepAsync(
                    "Open WebSocket, learn connectionId",
                    $"GET {webSocketUrl}\nUpgrade: websocket\nClientWebSocket.ConnectAsync(new Uri(\"{webSocketUrl}\"))\nSEND {{\"action\":\"whoami\"}}",
                    async () =>
                    {
                        socket = await ConnectAsync(webSocketUrl, ct).ConfigureAwait(false);
                        await SendTextAsync(socket, "{\"action\":\"whoami\"}", ct).ConfigureAwait(false);

                        string frame = await ReceiveTextAsync(socket, ct).ConfigureAwait(false);
                        using JsonDocument document = JsonDocument.Parse(frame);

                        if (!document.RootElement.TryGetProperty("connectionId", out JsonElement id) || id.GetString() is not { Length: > 0 } value)
                        {
                            throw new InvalidOperationException($"The server's reply to an unmatched frame carried no connectionId: {frame}");
                        }

                        connectionId = value;

                        return $"Connected (state: {socket.State}). RECEIVE {frame}";
                    }).ConfigureAwait(false);

                if (socket is null || connectionId is null)
                {
                    break;
                }

                ClientWebSocket connected = socket;

                using IAmazonApiGatewayManagementApi management = factory.CreateManagement(apiId, StageName);
                string managementUrl = factory.ManagementUrl(apiId, StageName);

                bool inspected = false;

                yield return await ApiGatewayV2Demo.RunStepAsync(
                    "GetConnection",
                    $"GET {managementUrl}/@connections/{connectionId}\nmanagement.GetConnectionAsync(new GetConnectionRequest {{ ConnectionId = \"{connectionId}\" }})",
                    async () =>
                    {
                        GetConnectionResponse response = await management.GetConnectionAsync(
                            new GetConnectionRequest { ConnectionId = connectionId }, ct).ConfigureAwait(false);
                        inspected = true;

                        return $"HTTP {(int)response.HttpStatusCode} — connectedAt: {response.ConnectedAt:O}, lastActiveAt: {response.LastActiveAt:O}, sourceIp: {response.Identity?.SourceIp}";
                    }).ConfigureAwait(false);

                if (!inspected)
                {
                    break;
                }

                bool pushed = false;

                // The round trip: a call made through the SDK, from the server side, has to arrive
                // as a frame on the socket the client opened. The client's receive is inside the
                // step so a push that "succeeds" without ever reaching the socket fails here.
                yield return await ApiGatewayV2Demo.RunStepAsync(
                    "PostToConnection (server push)",
                    $"POST {managementUrl}/@connections/{connectionId}\nmanagement.PostToConnectionAsync(new PostToConnectionRequest {{ ConnectionId = \"{connectionId}\", Data = \"{PushMessage}\" }})",
                    async () =>
                    {
                        using MemoryStream data = new(Encoding.UTF8.GetBytes(PushMessage));
                        PostToConnectionResponse response = await management.PostToConnectionAsync(
                            new PostToConnectionRequest { ConnectionId = connectionId, Data = data }, ct).ConfigureAwait(false);

                        string received = await ReceiveTextAsync(connected, ct).ConfigureAwait(false);

                        if (received != PushMessage)
                        {
                            throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — the client received a different frame: {received}");
                        }

                        pushed = true;

                        return $"HTTP {(int)response.HttpStatusCode} — the client received: {received}";
                    }).ConfigureAwait(false);

                if (!pushed)
                {
                    break;
                }

                yield return await ApiGatewayV2Demo.RunStepAsync(
                    "DeleteConnection (server disconnect)",
                    $"DELETE {managementUrl}/@connections/{connectionId}\nmanagement.DeleteConnectionAsync(new DeleteConnectionRequest {{ ConnectionId = \"{connectionId}\" }})",
                    async () =>
                    {
                        DeleteConnectionResponse response = await management.DeleteConnectionAsync(
                            new DeleteConnectionRequest { ConnectionId = connectionId }, ct).ConfigureAwait(false);

                        await WaitForCloseAsync(connected, ct).ConfigureAwait(false);

                        return $"HTTP {(int)response.HttpStatusCode} — the server closed the client's socket (state: {connected.State}, close status: {connected.CloseStatus})";
                    }).ConfigureAwait(false);
            }
            while (false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. Deleting the API drops its stage
            // and any connections with it.
            socket?.Dispose();
            cleanup = created ? await ApiGatewayV2Demo.DeleteApiAsync(factory.ServiceUrl, client, apiId, apiName).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    // Retries the handshake while real AWS finishes the auto-deploy CreateStage started (see
    // ApiGatewayV2Demo.DeployPollBudget); floci accepts the first attempt. A failed handshake
    // leaves its ClientWebSocket unusable, so each attempt gets a fresh one.
    private static async Task<ClientWebSocket> ConnectAsync(string webSocketUrl, CancellationToken ct)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(ApiGatewayV2Demo.DeployPollBudget.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            ClientWebSocket socket = new();

            try
            {
                await socket.ConnectAsync(new Uri(webSocketUrl), ct).ConfigureAwait(false);

                return socket;
            }
            catch (WebSocketException) when (Stopwatch.GetTimestamp() < deadline)
            {
                // Swallowed and retried: the stage may not be serving yet. Once the budget is
                // spent the filter no longer matches and the last handshake failure propagates.
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            await Task.Delay(ApiGatewayV2Demo.DeployPollDelay, ct).ConfigureAwait(false);
        }
    }

    private static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
        => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReceiveTimeout);

        byte[] buffer = new byte[8192];
        using MemoryStream message = new();

        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException($"The server closed the socket while a frame was expected (close status: {socket.CloseStatus}).");
                }

                message.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }
        // Only our own deadline becomes a failed step. The caller cancelling has to keep
        // propagating as cancellation, or navigating away would render as a red step.
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No frame arrived within {ReceiveTimeout.TotalSeconds:0} s.");
        }
    }

    // A server-initiated close only completes on the client once the client reads it, so this
    // drains frames until the Close arrives.
    private static async Task WaitForCloseAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReceiveTimeout);

        byte[] buffer = new byte[1024];

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The socket was still open {ReceiveTimeout.TotalSeconds:0} s after DeleteConnection.");
        }
        // The server may drop the TCP connection without a close handshake; either way the
        // connection is gone, which is what DeleteConnection promises.
        catch (WebSocketException)
        {
            // Swallowed: abrupt close is an acceptable way for a disconnect to arrive.
        }
    }
}
