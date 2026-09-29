using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.ServiceDiscovery;
using Amazon.ServiceDiscovery.Model;
using FlociLab.Core;

namespace FlociLab.Aws.CloudMap;

/// <summary>
/// Cloud Map namespaces, services and instances against floci. Ordinary AWSSDK.ServiceDiscovery
/// code — the only emulator-aware line in the sample is in <see cref="CloudMapClientFactory"/>.
/// </summary>
public sealed class CloudMapDemo(CloudMapClientFactory factory) : IServiceDemo
{
    // 192.0.2.0/24 is TEST-NET-1 (RFC 5737) and .test is reserved (RFC 2606): nothing registered
    // here can ever resolve to a real host, which matters when the page is pointed at a real account.
    private const string InstanceId = "web-1";
    private const string InstanceAddress = "192.0.2.10";
    private const string InstancePort = "8080";
    private const string StageAttribute = "stage";
    private const string StageValue = "blue";

    public string Provider => CloudProvider.Aws;

    public string Slug => "cloudmap";

    public string DisplayName => "Cloud Map";

    public string Category => "Networking";

    public string Route => "/aws/cloudmap";

    /// <summary>ListNamespaces — one request, no state, and the cheapest call Cloud Map has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonServiceDiscovery client = factory.Create();
            ListNamespacesResponse response = await client.ListNamespacesAsync(new ListNamespacesRequest(), ct).ConfigureAwait(false);
            int count = response.Namespaces?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListNamespaces returned {count} namespace(s) on the first page.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonServiceDiscovery client = factory.Create();

        // Unique per run, so two runs never collide and a namespace left by a crashed run never
        // makes the next one fail with NamespaceAlreadyExists.
        string run = Guid.NewGuid().ToString("N");
        string namespaceName = $"{run}.flocilab.test";
        const string ServiceName = "web";

        string? operationId = null;
        string? namespaceId = null;
        string? serviceId = null;
        bool namespaceRequested = false;
        bool instancePresent = false;

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "ListNamespaces — before",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.ListNamespaces\nclient.ListNamespacesAsync(new ListNamespacesRequest())",
                async () =>
                {
                    ListNamespacesResponse response = await client.ListNamespacesAsync(new ListNamespacesRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Namespaces?.Count ?? 0} namespace(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateHttpNamespace",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.CreateHttpNamespace\nclient.CreateHttpNamespaceAsync(new CreateHttpNamespaceRequest {{ Name = \"{namespaceName}\", CreatorRequestId = \"{run}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the namespace exists and cleanup has to go looking for it.
                    namespaceRequested = true;
                    CreateHttpNamespaceResponse response = await client.CreateHttpNamespaceAsync(
                        new CreateHttpNamespaceRequest { Name = namespaceName, CreatorRequestId = run }, ct).ConfigureAwait(false);

                    operationId = response.OperationId;

                    return $"HTTP {(int)response.HttpStatusCode} — operation {response.OperationId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetOperation — namespace created",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.GetOperation\nclient.GetOperationAsync(new GetOperationRequest {{ OperationId = \"{operationId}\" }})",
                async () =>
                {
                    // Cloud Map creates namespaces asynchronously — the CreateHttpNamespace answer
                    // is an operation id, not a namespace. The id of the namespace itself is only
                    // in the finished operation's targets.
                    Operation operation = await WaitForOperationAsync(client, operationId!, ct).ConfigureAwait(false);

                    namespaceId = operation.Targets[OperationTargetType.NAMESPACE.Value];

                    return $"{operation.Type} {operation.Status} — namespace {namespaceId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateService",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.CreateService\nclient.CreateServiceAsync(new CreateServiceRequest {{ Name = \"{ServiceName}\", NamespaceId = \"{namespaceId}\" }})",
                async () =>
                {
                    CreateServiceResponse response = await client.CreateServiceAsync(
                        new CreateServiceRequest { Name = ServiceName, NamespaceId = namespaceId, CreatorRequestId = run }, ct).ConfigureAwait(false);

                    serviceId = response.Service.Id;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Service.Id}, type {response.Service.Type}, {response.Service.InstanceCount} instance(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "RegisterInstance",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.RegisterInstance\nclient.RegisterInstanceAsync(… InstanceId = \"{InstanceId}\", AWS_INSTANCE_IPV4 = {InstanceAddress}, AWS_INSTANCE_PORT = {InstancePort}, {StageAttribute} = {StageValue})",
                async () =>
                {
                    // Set before the call for the same reason as the namespace above: a service
                    // cannot be deleted while it still holds an instance.
                    instancePresent = true;
                    RegisterInstanceResponse response = await client.RegisterInstanceAsync(
                        new RegisterInstanceRequest
                        {
                            ServiceId = serviceId,
                            InstanceId = InstanceId,
                            Attributes = new Dictionary<string, string>
                            {
                                ["AWS_INSTANCE_IPV4"] = InstanceAddress,
                                ["AWS_INSTANCE_PORT"] = InstancePort,
                                [StageAttribute] = StageValue,
                            },
                        }, ct).ConfigureAwait(false);

                    Operation operation = await WaitForOperationAsync(client, response.OperationId, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {operation.Type} {operation.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListInstances",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.ListInstances\nclient.ListInstancesAsync(new ListInstancesRequest {{ ServiceId = \"{serviceId}\" }})",
                async () =>
                {
                    ListInstancesResponse response = await client.ListInstancesAsync(new ListInstancesRequest { ServiceId = serviceId }, ct).ConfigureAwait(false);
                    InstanceSummary? instance = response.Instances.SingleOrDefault(i => i.Id == InstanceId);

                    // A list that does not contain what RegisterInstance wrote did not round-trip.
                    // The page promises what floci actually answered, so it goes out red rather
                    // than a green badge over an instance that is not there.
                    if (instance?.Attributes.GetValueOrDefault("AWS_INSTANCE_IPV4") != InstanceAddress)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — no instance {InstanceId} at {InstanceAddress}. Found: {string.Join(", ", response.Instances.Select(i => i.Id))}");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Instances.Count} instance(s)\n{Describe(instance.Attributes)}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                $"DiscoverInstances — {StageAttribute}={StageValue}",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.DiscoverInstances\nclient.DiscoverInstancesAsync(new DiscoverInstancesRequest {{ NamespaceName = \"{namespaceName}\", ServiceName = \"{ServiceName}\", QueryParameters = {{ {StageAttribute} = {StageValue} }} }})",
                async () =>
                {
                    // The data-plane call: an application asks by *name*, never by id, and filters
                    // on the attributes the instance registered with.
                    DiscoverInstancesResponse response = await client.DiscoverInstancesAsync(
                        DiscoverRequest(namespaceName, ServiceName, StageValue), ct).ConfigureAwait(false);
                    HttpInstanceSummary? instance = response.Instances.SingleOrDefault(i => i.InstanceId == InstanceId);

                    if (instance is null)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — {InstanceId} was not discovered with {StageAttribute}={StageValue}. Found {response.Instances.Count} instance(s).");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Instances.Count} instance(s), {instance.HealthStatus}\n{instance.InstanceId} {Describe(instance.Attributes)}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DiscoverInstances — no match",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.DiscoverInstances\nclient.DiscoverInstancesAsync(new DiscoverInstancesRequest {{ … QueryParameters = {{ {StageAttribute} = green }} }})",
                async () =>
                {
                    // A filter that matches nothing is an empty answer, not an error. Getting the
                    // instance back here would mean the attribute filter is being ignored.
                    DiscoverInstancesResponse response = await client.DiscoverInstancesAsync(
                        DiscoverRequest(namespaceName, ServiceName, "green"), ct).ConfigureAwait(false);

                    if (response.Instances.Count != 0)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — {response.Instances.Count} instance(s) came back for {StageAttribute}=green; the filter was ignored.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — 0 instances (the filter excluded {InstanceId})";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteNamespace — while it holds a service",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.DeleteNamespace\nclient.DeleteNamespaceAsync(new DeleteNamespaceRequest {{ Id = \"{namespaceId}\" }})",
                async () =>
                {
                    // A namespace is not deletable while it still holds a service. Refusal is the
                    // passing outcome. A successful delete removed the namespace, but nothing says
                    // it took the service and instance with it — so only the namespace id is
                    // dropped, and the steps below and the finally still remove whatever is left.
                    try
                    {
                        DeleteNamespaceResponse response = await client.DeleteNamespaceAsync(
                            new DeleteNamespaceRequest { Id = namespaceId }, ct).ConfigureAwait(false);

                        namespaceId = null;

                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — the namespace was deleted although it still held a service.");
                    }
                    catch (ResourceInUseException ex)
                    {
                        return $"HTTP {(int)ex.StatusCode} — {ex.ErrorCode}: {ex.Message}\n(refused, as real Cloud Map does)";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeregisterInstance",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.DeregisterInstance\nclient.DeregisterInstanceAsync(new DeregisterInstanceRequest {{ ServiceId = \"{serviceId}\", InstanceId = \"{InstanceId}\" }})",
                async () =>
                {
                    DeregisterInstanceResponse response = await client.DeregisterInstanceAsync(
                        new DeregisterInstanceRequest { ServiceId = serviceId, InstanceId = InstanceId }, ct).ConfigureAwait(false);

                    // Deregistration is asynchronous too, and DeleteService is refused until it
                    // finishes — so the operation is waited on before the flag is cleared.
                    Operation operation = await WaitForOperationAsync(client, response.OperationId, ct).ConfigureAwait(false);
                    instancePresent = false;

                    return $"HTTP {(int)response.HttpStatusCode} — {operation.Type} {operation.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteService",
                $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.DeleteService\nclient.DeleteServiceAsync(new DeleteServiceRequest {{ Id = \"{serviceId}\" }})",
                async () =>
                {
                    DeleteServiceResponse response = await client.DeleteServiceAsync(new DeleteServiceRequest { Id = serviceId }, ct).ConfigureAwait(false);

                    serviceId = null;

                    return $"HTTP {(int)response.HttpStatusCode} — service removed";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The steps it produces are yielded
            // below — an iterator may not yield from inside a finally.
            if (namespaceRequested && namespaceId is null)
            {
                namespaceId = await this.FindNamespaceIdAsync(client, namespaceName).ConfigureAwait(false);
            }

            if (serviceId is null && namespaceId is not null)
            {
                serviceId = await this.FindServiceIdAsync(client, namespaceId, ServiceName).ConfigureAwait(false);
            }

            if (serviceId is not null && instancePresent)
            {
                cleanup.Add(await this.DeregisterAsync(client, serviceId, ct).ConfigureAwait(false));
            }

            if (serviceId is not null)
            {
                cleanup.Add(await this.DeleteServiceAsync(client, serviceId, ct).ConfigureAwait(false));
            }

            if (namespaceId is not null)
            {
                cleanup.Add(await this.DeleteNamespaceAsync(client, namespaceId, ct).ConfigureAwait(false));
            }
        }

        foreach (DemoStep step in cleanup)
        {
            yield return step;
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
                    return ProbeResult.NotImplemented(DescribeError(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(DescribeError(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(DescribeError(ex), elapsed);
            }
        }

        return ProbeResult.Error(DescribeError(ex), elapsed);
    }

    private static DiscoverInstancesRequest DiscoverRequest(string namespaceName, string serviceName, string stage)
        => new()
        {
            NamespaceName = namespaceName,
            ServiceName = serviceName,
            QueryParameters = new Dictionary<string, string> { [StageAttribute] = stage },
        };

    private static string Describe(Dictionary<string, string> attributes)
        => string.Join(", ", attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={a.Value}"));

    /// <summary>
    /// Polls an asynchronous Cloud Map operation to a terminal state. Real Cloud Map takes seconds
    /// to a minute; floci answers SUCCESS at once, so against the emulator the loop never waits.
    /// The 90 s budget is sized for real Cloud Map, which is the case it exists for.
    /// </summary>
    private static async Task<Operation> WaitForOperationAsync(IAmazonServiceDiscovery client, string operationId, CancellationToken ct)
    {
        GetOperationResponse response = await client.GetOperationAsync(new GetOperationRequest { OperationId = operationId }, ct).ConfigureAwait(false);

        for (int attempt = 0; attempt < 18 && response.Operation.Status != OperationStatus.SUCCESS && response.Operation.Status != OperationStatus.FAIL; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            response = await client.GetOperationAsync(new GetOperationRequest { OperationId = operationId }, ct).ConfigureAwait(false);
        }

        if (response.Operation.Status != OperationStatus.SUCCESS)
        {
            throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — {operationId} ({response.Operation.Type}) was {response.Operation.Status} {response.Operation.ErrorMessage}".TrimEnd());
        }

        return response.Operation;
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Cloud Map would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the
        // namespace. Catching it here would instead fabricate a "Failed" step for every remaining
        // operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string DescribeError(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Recovers the namespace id when the run failed between CreateHttpNamespace and reading it
    /// back from the operation — the namespace exists but the run never learned its id. Cleanup
    /// deliberately uses <see cref="CancellationToken.None"/> throughout: a cancelled run still has
    /// state to remove.
    /// </summary>
    private async Task<string?> FindNamespaceIdAsync(IAmazonServiceDiscovery client, string namespaceName)
    {
        try
        {
            // Every page, not the first: on a real account with more namespaces than one page
            // holds, the run's own namespace can be on any of them.
            string? nextToken = null;

            do
            {
                ListNamespacesResponse response = await client.ListNamespacesAsync(new ListNamespacesRequest { NextToken = nextToken }, CancellationToken.None).ConfigureAwait(false);
                NamespaceSummary? match = response.Namespaces?.SingleOrDefault(n => n.Name == namespaceName);

                if (match is not null)
                {
                    return match.Id;
                }

                nextToken = response.NextToken;
            }
            while (!string.IsNullOrEmpty(nextToken));

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing to look up when the emulator is gone; the failed step already says so.
            return null;
        }
    }

    private async Task<string?> FindServiceIdAsync(IAmazonServiceDiscovery client, string namespaceId, string serviceName)
    {
        try
        {
            ListServicesResponse response = await client.ListServicesAsync(
                new ListServicesRequest { Filters = [new ServiceFilter { Name = ServiceFilterName.NAMESPACE_ID, Condition = FilterCondition.EQ, Values = [namespaceId] }] },
                CancellationToken.None).ConfigureAwait(false);

            return response.Services.SingleOrDefault(s => s.Name == serviceName)?.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning as FindNamespaceIdAsync.
            return null;
        }
    }

    private async Task<DemoStep> DeregisterAsync(IAmazonServiceDiscovery client, string serviceId, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.DeregisterInstance\nclient.DeregisterInstanceAsync(new DeregisterInstanceRequest {{ ServiceId = \"{serviceId}\", InstanceId = \"{InstanceId}\" }})";

        return await RunStepAsync("DeregisterInstance — cleanup", request, async () =>
        {
            try
            {
                DeregisterInstanceResponse response = await client.DeregisterInstanceAsync(
                    new DeregisterInstanceRequest { ServiceId = serviceId, InstanceId = InstanceId }, CancellationToken.None).ConfigureAwait(false);
                await WaitForOperationAsync(client, response.OperationId, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — removed the instance"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (InstanceNotFoundException)
            {
                return "Nothing to remove — the instance was never registered.";
            }
        }).ConfigureAwait(false);
    }

    private async Task<DemoStep> DeleteServiceAsync(IAmazonServiceDiscovery client, string serviceId, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.DeleteService\nclient.DeleteServiceAsync(new DeleteServiceRequest {{ Id = \"{serviceId}\" }})";

        return await RunStepAsync("DeleteService — cleanup", request, async () =>
        {
            try
            {
                DeleteServiceResponse response = await client.DeleteServiceAsync(new DeleteServiceRequest { Id = serviceId }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — removed the service"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (ServiceNotFoundException)
            {
                return "Nothing to remove — the service was never created.";
            }
        }).ConfigureAwait(false);
    }

    private async Task<DemoStep> DeleteNamespaceAsync(IAmazonServiceDiscovery client, string namespaceId, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/  X-Amz-Target: Route53AutoNaming_v20170314.DeleteNamespace\nclient.DeleteNamespaceAsync(new DeleteNamespaceRequest {{ Id = \"{namespaceId}\" }})";

        return await RunStepAsync("DeleteNamespace — cleanup", request, async () =>
        {
            try
            {
                DeleteNamespaceResponse response = await client.DeleteNamespaceAsync(new DeleteNamespaceRequest { Id = namespaceId }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — removed the namespace"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (NamespaceNotFoundException)
            {
                return "Nothing to remove — the namespace was never created.";
            }
        }).ConfigureAwait(false);
    }
}
