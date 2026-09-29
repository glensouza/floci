using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.ElasticLoadBalancing;
using Amazon.ElasticLoadBalancing.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.ElbClassic;

/// <summary>
/// A classic load balancer with two listeners, a health check, a registered instance, attributes
/// and tags against floci. Ordinary AWSSDK.ElasticLoadBalancing code — the only emulator-aware line
/// in the sample is in <see cref="ElbClassicClientFactory"/>.
/// </summary>
public sealed class ElbClassicDemo(ElbClassicClientFactory factory) : IServiceDemo
{
    // An id in the right shape and nothing behind it, so the sample needs no instance and no second
    // service (EC2 would be a second SDK package). floci only records the id; real AWS rejects it
    // with InvalidInstance, so against real AWS substitute a running instance in the same region
    // (docs/BLAZOR-PLAN.md §14).
    private const string InstanceId = "i-0123456789abcdef0";

    private const int IdleTimeoutSeconds = 120;

    public string Provider => CloudProvider.Aws;

    public string Slug => "elbclassic";

    public string DisplayName => "ELB Classic";

    public string Category => "Networking";

    public string Route => "/aws/elbclassic";

    /// <summary>
    /// DescribeLoadBalancers. A fresh account lists nothing, and an empty list is exactly the shape
    /// that trips AWSSDK collection handling (plan §14, the IAM row), so the count is read through
    /// null-conditionals and zero is a perfectly good answer.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonElasticLoadBalancing client = factory.Create();
            DescribeLoadBalancersResponse response = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest { PageSize = 1 }, ct).ConfigureAwait(false);
            int count = response.LoadBalancerDescriptions?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"DescribeLoadBalancers returned {count} load balancer(s) on the first page.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonElasticLoadBalancing client = factory.Create();

        // Unique per run, so two runs never collide and a load balancer left by a crashed run never
        // makes the next one fail with DuplicateLoadBalancerName. The name limit is 32 characters,
        // which 12 hex digits stays well inside.
        string name = $"flocilab-clb-{Guid.NewGuid().ToString("N")[..12]}";
        string url = factory.ServiceUrl;
        string zone = factory.AvailabilityZone;

        bool createAttempted = false;
        bool deleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "DescribeLoadBalancers — before",
                $"POST {url}/\nAction=DescribeLoadBalancers\nclient.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest())",
                async () =>
                {
                    DescribeLoadBalancersResponse response = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.LoadBalancerDescriptions?.Count ?? 0} load balancer(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateLoadBalancer",
                $"POST {url}/\nAction=CreateLoadBalancer\nclient.CreateLoadBalancerAsync(new CreateLoadBalancerRequest {{ LoadBalancerName = \"{name}\", Listeners = [HTTP:80 -> HTTP:8080], AvailabilityZones = [{zone}], Tags = [lab=flocilab] }})",
                async () =>
                {
                    // Claimed before the call, not after: a request that lands while its response is
                    // lost (the page's Dispose cancels mid-flight) still created a load balancer.
                    // Cleanup asks the server rather than trusting this flag (plan §14).
                    createAttempted = true;

                    CreateLoadBalancerResponse response = await client.CreateLoadBalancerAsync(
                        new CreateLoadBalancerRequest
                        {
                            LoadBalancerName = name,
                            Listeners = [new Listener { Protocol = "HTTP", LoadBalancerPort = 80, InstanceProtocol = "HTTP", InstancePort = 8080 }],
                            AvailabilityZones = [zone],
                            Tags = [new Tag { Key = "lab", Value = "flocilab" }],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {name}, {response.DNSName}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateLoadBalancerListeners",
                $"POST {url}/\nAction=CreateLoadBalancerListeners\nclient.CreateLoadBalancerListenersAsync(new CreateLoadBalancerListenersRequest {{ LoadBalancerName = \"{name}\", Listeners = [TCP:8443 -> TCP:8443] }})",
                async () =>
                {
                    CreateLoadBalancerListenersResponse response = await client.CreateLoadBalancerListenersAsync(
                        new CreateLoadBalancerListenersRequest
                        {
                            LoadBalancerName = name,
                            Listeners = [new Listener { Protocol = "TCP", LoadBalancerPort = 8443, InstanceProtocol = "TCP", InstancePort = 8443 }],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ConfigureHealthCheck",
                $"POST {url}/\nAction=ConfigureHealthCheck\nclient.ConfigureHealthCheckAsync(new ConfigureHealthCheckRequest {{ LoadBalancerName = \"{name}\", HealthCheck = {{ Target = HTTP:8080/health, Interval = 30, Timeout = 5, HealthyThreshold = 3, UnhealthyThreshold = 2 }} }})",
                async () =>
                {
                    ConfigureHealthCheckResponse response = await client.ConfigureHealthCheckAsync(
                        new ConfigureHealthCheckRequest
                        {
                            LoadBalancerName = name,
                            HealthCheck = new HealthCheck { Target = "HTTP:8080/health", Interval = 30, Timeout = 5, HealthyThreshold = 3, UnhealthyThreshold = 2 },
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.HealthCheck?.Target}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "RegisterInstancesWithLoadBalancer",
                $"POST {url}/\nAction=RegisterInstancesWithLoadBalancer\nclient.RegisterInstancesWithLoadBalancerAsync(new RegisterInstancesWithLoadBalancerRequest {{ LoadBalancerName = \"{name}\", Instances = [{InstanceId}] }})",
                async () =>
                {
                    RegisterInstancesWithLoadBalancerResponse response = await client.RegisterInstancesWithLoadBalancerAsync(
                        new RegisterInstancesWithLoadBalancerRequest
                        {
                            LoadBalancerName = name,
                            Instances = [new Instance { InstanceId = InstanceId }],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", (response.Instances ?? []).Select(i => i.InstanceId))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeInstanceHealth",
                $"POST {url}/\nAction=DescribeInstanceHealth\nclient.DescribeInstanceHealthAsync(new DescribeInstanceHealthRequest {{ LoadBalancerName = \"{name}\" }})",
                async () =>
                {
                    DescribeInstanceHealthResponse response = await client.DescribeInstanceHealthAsync(new DescribeInstanceHealthRequest { LoadBalancerName = name }, ct).ConfigureAwait(false);
                    List<InstanceState> states = response.InstanceStates ?? [];

                    // The state is reported, not asserted: on real AWS it depends on health checks
                    // against a live instance, which take a few intervals to settle. What this proves
                    // is that the registration is visible.
                    if (!states.Exists(s => s.InstanceId == InstanceId))
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — {InstanceId} was registered but the load balancer reports {states.Count} instance(s).");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join("; ", states.Select(s => $"{s.InstanceId} {s.State} ({s.ReasonCode}: {s.Description})"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeLoadBalancers — after configuring",
                $"POST {url}/\nAction=DescribeLoadBalancers\nclient.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest {{ LoadBalancerNames = [\"{name}\"] }})",
                async () =>
                {
                    DescribeLoadBalancersResponse response = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest { LoadBalancerNames = [name] }, ct).ConfigureAwait(false);
                    LoadBalancerDescription? description = response.LoadBalancerDescriptions?.FirstOrDefault();
                    List<ListenerDescription> listeners = description?.ListenerDescriptions ?? [];

                    // Everything the steps above wrote has to read back from the one description.
                    if (description is null
                        || listeners.Count != 2
                        || description.HealthCheck?.Target != "HTTP:8080/health"
                        || !(description.Instances ?? []).Exists(i => i.InstanceId == InstanceId))
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected 2 listeners, the health check and {InstanceId} but got {listeners.Count} listener(s), check {description?.HealthCheck?.Target ?? "none"}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", listeners.Select(l => $"{l.Listener.Protocol}:{l.Listener.LoadBalancerPort}->{l.Listener.InstancePort}"))}; health check {description.HealthCheck.Target}; zones {string.Join(", ", description.AvailabilityZones ?? [])}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ModifyLoadBalancerAttributes",
                $"POST {url}/\nAction=ModifyLoadBalancerAttributes\nclient.ModifyLoadBalancerAttributesAsync(new ModifyLoadBalancerAttributesRequest {{ LoadBalancerName = \"{name}\", CrossZoneLoadBalancing = true, IdleTimeout = {IdleTimeoutSeconds} }})",
                async () =>
                {
                    ModifyLoadBalancerAttributesResponse response = await client.ModifyLoadBalancerAttributesAsync(
                        new ModifyLoadBalancerAttributesRequest
                        {
                            LoadBalancerName = name,
                            LoadBalancerAttributes = new LoadBalancerAttributes
                            {
                                CrossZoneLoadBalancing = new CrossZoneLoadBalancing { Enabled = true },
                                ConnectionSettings = new ConnectionSettings { IdleTimeout = IdleTimeoutSeconds },
                            },
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeLoadBalancerAttributes",
                $"POST {url}/\nAction=DescribeLoadBalancerAttributes\nclient.DescribeLoadBalancerAttributesAsync(new DescribeLoadBalancerAttributesRequest {{ LoadBalancerName = \"{name}\" }})",
                async () =>
                {
                    DescribeLoadBalancerAttributesResponse response = await client.DescribeLoadBalancerAttributesAsync(new DescribeLoadBalancerAttributesRequest { LoadBalancerName = name }, ct).ConfigureAwait(false);
                    LoadBalancerAttributes? attributes = response.LoadBalancerAttributes;

                    if (attributes?.CrossZoneLoadBalancing?.Enabled != true || attributes.ConnectionSettings?.IdleTimeout != IdleTimeoutSeconds)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected cross-zone on and idle timeout {IdleTimeoutSeconds} but got {attributes?.CrossZoneLoadBalancing?.Enabled} and {attributes?.ConnectionSettings?.IdleTimeout}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — cross-zone {attributes.CrossZoneLoadBalancing.Enabled}, idle timeout {attributes.ConnectionSettings.IdleTimeout}s, connection draining {attributes.ConnectionDraining?.Enabled}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AddTags",
                $"POST {url}/\nAction=AddTags\nclient.AddTagsAsync(new AddTagsRequest {{ LoadBalancerNames = [\"{name}\"], Tags = [episode=elb-classic] }})",
                async () =>
                {
                    AddTagsResponse response = await client.AddTagsAsync(
                        new AddTagsRequest { LoadBalancerNames = [name], Tags = [new Tag { Key = "episode", Value = "elb-classic" }] }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeTags",
                $"POST {url}/\nAction=DescribeTags\nclient.DescribeTagsAsync(new DescribeTagsRequest {{ LoadBalancerNames = [\"{name}\"] }})",
                async () =>
                {
                    DescribeTagsResponse response = await client.DescribeTagsAsync(new DescribeTagsRequest { LoadBalancerNames = [name] }, ct).ConfigureAwait(false);
                    List<Tag> tags = response.TagDescriptions?.FirstOrDefault()?.Tags ?? [];

                    // One tag from CreateLoadBalancer and one from AddTags: both routes end up in the same set.
                    if (tags.Find(t => t.Key == "lab")?.Value != "flocilab" || tags.Find(t => t.Key == "episode")?.Value != "elb-classic")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected lab=flocilab and episode=elb-classic but got: {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteLoadBalancerListeners",
                $"POST {url}/\nAction=DeleteLoadBalancerListeners\nclient.DeleteLoadBalancerListenersAsync(new DeleteLoadBalancerListenersRequest {{ LoadBalancerName = \"{name}\", LoadBalancerPorts = [8443] }})",
                async () =>
                {
                    DeleteLoadBalancerListenersResponse response = await client.DeleteLoadBalancerListenersAsync(
                        new DeleteLoadBalancerListenersRequest { LoadBalancerName = name, LoadBalancerPorts = [8443] }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeregisterInstancesFromLoadBalancer",
                $"POST {url}/\nAction=DeregisterInstancesFromLoadBalancer\nclient.DeregisterInstancesFromLoadBalancerAsync(new DeregisterInstancesFromLoadBalancerRequest {{ LoadBalancerName = \"{name}\", Instances = [{InstanceId}] }})",
                async () =>
                {
                    DeregisterInstancesFromLoadBalancerResponse response = await client.DeregisterInstancesFromLoadBalancerAsync(
                        new DeregisterInstancesFromLoadBalancerRequest
                        {
                            LoadBalancerName = name,
                            Instances = [new Instance { InstanceId = InstanceId }],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {(response.Instances ?? []).Count} instance(s) remain";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteLoadBalancer",
                $"POST {url}/\nAction=DeleteLoadBalancer\nclient.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest {{ LoadBalancerName = \"{name}\" }})",
                async () =>
                {
                    DeleteLoadBalancerResponse response = await client.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest { LoadBalancerName = name }, ct).ConfigureAwait(false);

                    deleted = true;

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            if (createAttempted && !deleted)
            {
                cleanup.Add(await this.DeleteLoadBalancerAsync(client, name, ct).ConfigureAwait(false));
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

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Elastic Load Balancing would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes what exists.
        // Catching it here would instead fabricate a "Failed" step for every remaining
        // operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// Cleanup uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// load balancer to remove. Classic DeleteLoadBalancer is idempotent (a missing name answers
    /// 200, floci and AWS alike), so its status proves nothing: a create that never landed would
    /// still show a green "deleted". The server is asked first instead — LoadBalancerNotFound is
    /// proof there is nothing to remove, and anything else is deleted (plan §14, third corollary).
    /// </summary>
    private async Task<DemoStep> DeleteLoadBalancerAsync(IAmazonElasticLoadBalancing client, string name, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nAction=DescribeLoadBalancers, then DeleteLoadBalancer\nclient.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest {{ LoadBalancerName = \"{name}\" }})";

        return await RunStepAsync("DeleteLoadBalancer — cleanup", request, async () =>
        {
            try
            {
                await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest { LoadBalancerNames = [name] }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AccessPointNotFoundException)
            {
                // Not a failure: the create never landed, and the server says so.
                return $"No load balancer named {name} exists — nothing to remove.";
            }

            DeleteLoadBalancerResponse response = await client.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest { LoadBalancerName = name }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — deleted"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
