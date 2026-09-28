using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayV2;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.ApiGatewayV2;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; API Gateway needs none of its own — every management-plane
/// operation already addresses the single base endpoint. Nothing else about the SDK usage differs
/// from production.
/// </summary>
public sealed class ApiGatewayV2ClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// The backend the HTTP API proxies to. An HTTP API has no MOCK integration (that is REST-only),
    /// so the route needs a real upstream to answer. Against floci that is its own health endpoint,
    /// addressed as the emulator sees itself — floci proxies from inside its container, where it
    /// always listens on 4566, not on whatever host port the container was published on (a
    /// Testcontainers run gets a random one). Real API Gateway calls out from
    /// AWS's network, where <c>127.0.0.1</c> means nothing, so it gets a public page instead.
    /// </summary>
    public string IntegrationUri
        => this.UseEmulator ? "http://127.0.0.1:4566/_floci/health" : "https://example.com/";

    /// <summary>Text the upstream's body always contains — proof the proxy really forwarded.</summary>
    public string IntegrationMarker => this.UseEmulator ? "\"services\"" : "Example Domain";

    /// <summary>
    /// The invoke URL for a deployed stage. Real API Gateway serves invocations from a separate
    /// <c>execute-api</c> domain, never from the management endpoint the SDK calls above use —
    /// that split is how the real service is shaped, not an emulator quirk, so this switches on
    /// <see cref="UseEmulator"/> rather than reusing <see cref="ServiceUrl"/> unconditionally.
    /// floci serves HTTP APIs from its own management endpoint under the same
    /// <c>/restapis/{id}/{stage}/_user_request_/{path}</c> convention it uses for REST APIs.
    /// </summary>
    public string InvokeUrl(string apiId, string stageName, string routePath)
        => this.UseEmulator
            ? $"{this.ServiceUrl}/restapis/{apiId}/{stageName}/_user_request_/{routePath}"
            : $"https://{apiId}.execute-api.{endpoints.Region}.amazonaws.com/{stageName}/{routePath}";

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonApiGatewayV2 Create()
    {
        // Real AWS. The credentials go too — the SDK's own chain (environment, profile, SSO, IMDS)
        // is what a production app uses, and the static "test"/"test" pair would be rejected.
        // Retries come back to the SDK default, because the reason they were off is a
        // lab-ergonomics one that does not apply here.
        if (!endpoints.UseEmulator)
        {
            return new AmazonApiGatewayV2Client(new AmazonApiGatewayV2Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonApiGatewayV2Config config = new AmazonApiGatewayV2Config
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s and a whole run into ~49 s of "Running…". Two
            // reasons to turn it off here: a page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request — silently sending five would make the page lie about the wire.
            // A production app against real API Gateway wants the retries; this is the second and
            // last emulator-shaped line in the sample.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonApiGatewayV2Client(endpoints.Credentials(), config);
    }

    /// <summary>
    /// Where a client opens the WebSocket for a stage. Real API Gateway hands out
    /// <c>wss://{apiId}.execute-api.{region}.amazonaws.com/{stage}</c>; floci accepts that host
    /// form only through its own DNS suffix, so the explicit path form on the management port is
    /// used instead — it needs no name resolution and works from a host that has never seen it.
    /// </summary>
    public string WebSocketUrl(string apiId, string stageName)
        => this.UseEmulator
            ? $"{(this.ServiceUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws")}://{new Uri(this.ServiceUrl).Authority}/ws/{apiId}/{stageName}"
            : $"wss://{apiId}.execute-api.{endpoints.Region}.amazonaws.com/{stageName}";

    /// <summary>
    /// The base address of the connection-management API for one stage — the SDK appends
    /// <c>/@connections/{connectionId}</c>. Unlike every other client in this sample it is scoped
    /// to a single API and stage, because that is how AWS shapes it: there is no shared
    /// regional endpoint for <c>@connections</c>.
    /// </summary>
    public string ManagementUrl(string apiId, string stageName)
        => this.UseEmulator
            ? $"{this.ServiceUrl}/execute-api/{apiId}/{stageName}"
            : $"https://{apiId}.execute-api.{endpoints.Region}.amazonaws.com/{stageName}";

    /// <summary>The <c>@connections</c> client for one deployed WebSocket stage.</summary>
    public IAmazonApiGatewayManagementApi CreateManagement(string apiId, string stageName)
    {
        string url = this.ManagementUrl(apiId, stageName);

        if (!endpoints.UseEmulator)
        {
            return new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig { ServiceURL = url });
        }

        // ForFloci points the config at the base endpoint; the per-stage address then replaces it.
        AmazonApiGatewayManagementApiConfig config = new AmazonApiGatewayManagementApiConfig { MaxErrorRetry = 0 }.ForFloci(endpoints);
        config.ServiceURL = url;

        return new AmazonApiGatewayManagementApiClient(endpoints.Credentials(), config);
    }
}
