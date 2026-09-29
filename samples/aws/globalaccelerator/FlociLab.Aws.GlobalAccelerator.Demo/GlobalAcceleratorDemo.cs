using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.GlobalAccelerator;
using Amazon.GlobalAccelerator.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.GlobalAccelerator;

/// <summary>
/// An accelerator with a listener, an endpoint group and a weighted endpoint, tagged, refused twice
/// on delete and then taken apart, against floci. Ordinary AWSSDK.GlobalAccelerator code — the only
/// emulator-aware line in the sample is in <see cref="GlobalAcceleratorClientFactory"/>.
/// </summary>
public sealed class GlobalAcceleratorDemo(GlobalAcceleratorClientFactory factory) : IServiceDemo
{
    // An endpoint id in the right shape and nothing behind it, so the sample needs no load balancer
    // and no second service (ELB v2 would be a second SDK package). floci accepts it; real Global
    // Accelerator rejects an ARN it cannot resolve, so against real AWS substitute an ALB, NLB, EC2
    // instance or Elastic IP in the endpoint group's region (docs/BLAZOR-PLAN.md §14).
    private const string EndpointId = "arn:aws:elasticloadbalancing:us-east-1:000000000000:loadbalancer/app/flocilab/0123456789abcdef";

    private const string EndpointGroupRegion = "us-east-1";

    // Real accelerators take minutes to move IN_PROGRESS -> DEPLOYED after a change; floci is
    // DEPLOYED at once. The ceiling is generous because the cost of waiting is a page that says
    // "Running…", and the cost of not waiting is a leaked accelerator that bills by the hour.
    private static readonly TimeSpan DeployTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DeployPollInterval = TimeSpan.FromSeconds(5);

    public string Provider => CloudProvider.Aws;

    public string Slug => "globalaccelerator";

    public string DisplayName => "Global Accelerator";

    public string Category => "Networking";

    public string Route => "/aws/globalaccelerator";

    /// <summary>
    /// ListAccelerators. The IAM row in plan §14 says never to probe with a list that is empty on a
    /// fresh account, but Global Accelerator has no call that is not — there is no managed catalog
    /// like IAM's AWS-scoped policies. It is a JSON service, not the XML unmarshaller that row was
    /// about, and <c>Probe_Reports_Ok</c> runs against a fresh container, so the empty case is
    /// covered; the count is still read through null-conditionals and zero is a good answer.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonGlobalAccelerator client = factory.Create();
            ListAcceleratorsResponse response = await client.ListAcceleratorsAsync(new ListAcceleratorsRequest { MaxResults = 1 }, ct).ConfigureAwait(false);
            int count = response.Accelerators?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListAccelerators returned {count} accelerator(s) on the first page.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonGlobalAccelerator client = factory.Create();

        // Unique per run, so two runs never collide, and — because the ARNs are minted by the
        // server — the one handle cleanup still has if a create landed while its response was lost.
        string name = $"flocilab-ga-{Guid.NewGuid().ToString("N")[..12]}";
        string url = factory.ServiceUrl;

        string acceleratorArn = string.Empty;
        string listenerArn = string.Empty;
        string endpointGroupArn = string.Empty;

        bool createAttempted = false;
        bool deleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "ListAccelerators — before",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.ListAccelerators\nclient.ListAcceleratorsAsync(new ListAcceleratorsRequest())",
                async () =>
                {
                    ListAcceleratorsResponse response = await client.ListAcceleratorsAsync(new ListAcceleratorsRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Accelerators?.Count ?? 0} accelerator(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateAccelerator",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.CreateAccelerator\nclient.CreateAcceleratorAsync(new CreateAcceleratorRequest {{ Name = \"{name}\", IpAddressType = IPV4, Enabled = true, IdempotencyToken = <guid>, Tags = [lab=flocilab] }})",
                async () =>
                {
                    // Claimed before the call, not after: a request that lands while its response is
                    // lost (the page's Dispose cancels mid-flight) still created an accelerator.
                    // Cleanup asks the server, by name, rather than trusting this flag (plan §14).
                    createAttempted = true;

                    CreateAcceleratorResponse response = await client.CreateAcceleratorAsync(
                        new CreateAcceleratorRequest
                        {
                            Name = name,
                            IpAddressType = IpAddressType.IPV4,
                            Enabled = true,
                            IdempotencyToken = Guid.NewGuid().ToString(),
                            Tags = [new Tag { Key = "lab", Value = "flocilab" }],
                        }, ct).ConfigureAwait(false);

                    acceleratorArn = response.Accelerator.AcceleratorArn;

                    return $"HTTP {(int)response.HttpStatusCode} — {acceleratorArn}, status {response.Accelerator.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeAccelerator — static addresses",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.DescribeAccelerator\nclient.DescribeAcceleratorAsync(new DescribeAcceleratorRequest {{ AcceleratorArn = \"{acceleratorArn}\" }})",
                async () =>
                {
                    Accelerator accelerator = await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);
                    List<string> addresses = (accelerator.IpSets ?? []).SelectMany(s => s.IpAddresses ?? []).ToList();

                    // Two static anycast addresses per address family is the product: they are what a
                    // customer allow-lists, and they outlive every endpoint behind them.
                    if (addresses.Count != 2)
                    {
                        throw new InvalidOperationException($"expected 2 static IPv4 addresses but got {addresses.Count}: {string.Join(", ", addresses)}.");
                    }

                    return $"{accelerator.Status} — {string.Join(" and ", addresses)}, DNS {accelerator.DnsName}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeAcceleratorAttributes",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.DescribeAcceleratorAttributes\nclient.DescribeAcceleratorAttributesAsync(new DescribeAcceleratorAttributesRequest {{ AcceleratorArn = \"{acceleratorArn}\" }})",
                async () =>
                {
                    DescribeAcceleratorAttributesResponse response = await client.DescribeAcceleratorAttributesAsync(new DescribeAcceleratorAttributesRequest { AcceleratorArn = acceleratorArn }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — flow logs {(response.AcceleratorAttributes?.FlowLogsEnabled == true ? "on" : "off")}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateListener",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.CreateListener\nclient.CreateListenerAsync(new CreateListenerRequest {{ AcceleratorArn = \"{acceleratorArn}\", Protocol = TCP, PortRanges = [80-80], ClientAffinity = NONE }})",
                async () =>
                {
                    CreateListenerResponse response = await client.CreateListenerAsync(
                        new CreateListenerRequest
                        {
                            AcceleratorArn = acceleratorArn,
                            Protocol = Protocol.TCP,
                            PortRanges = [new PortRange { FromPort = 80, ToPort = 80 }],
                            ClientAffinity = ClientAffinity.NONE,
                            IdempotencyToken = Guid.NewGuid().ToString(),
                        }, ct).ConfigureAwait(false);

                    listenerArn = response.Listener.ListenerArn;

                    await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {listenerArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateListener",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.UpdateListener\nclient.UpdateListenerAsync(new UpdateListenerRequest {{ ListenerArn = \"{listenerArn}\", Protocol = TCP, PortRanges = [80-80, 443-443], ClientAffinity = SOURCE_IP }})",
                async () =>
                {
                    UpdateListenerResponse response = await client.UpdateListenerAsync(
                        new UpdateListenerRequest
                        {
                            ListenerArn = listenerArn,
                            Protocol = Protocol.TCP,
                            PortRanges = [new PortRange { FromPort = 80, ToPort = 80 }, new PortRange { FromPort = 443, ToPort = 443 }],
                            ClientAffinity = ClientAffinity.SOURCE_IP,
                        }, ct).ConfigureAwait(false);

                    await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);
                    List<PortRange> ranges = response.Listener.PortRanges ?? [];

                    if (ranges.Count != 2 || response.Listener.ClientAffinity != ClientAffinity.SOURCE_IP)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected 2 port ranges and SOURCE_IP but got {ranges.Count} and {response.Listener.ClientAffinity}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", ranges.Select(r => $"{r.FromPort}-{r.ToPort}"))}, affinity {response.Listener.ClientAffinity}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateEndpointGroup",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.CreateEndpointGroup\nclient.CreateEndpointGroupAsync(new CreateEndpointGroupRequest {{ ListenerArn = \"{listenerArn}\", EndpointGroupRegion = \"{EndpointGroupRegion}\", TrafficDialPercentage = 50, HealthCheckProtocol = TCP, HealthCheckPort = 80 }})",
                async () =>
                {
                    CreateEndpointGroupResponse response = await client.CreateEndpointGroupAsync(
                        new CreateEndpointGroupRequest
                        {
                            ListenerArn = listenerArn,
                            EndpointGroupRegion = EndpointGroupRegion,
                            TrafficDialPercentage = 50,
                            HealthCheckProtocol = HealthCheckProtocol.TCP,
                            HealthCheckPort = 80,
                            IdempotencyToken = Guid.NewGuid().ToString(),
                        }, ct).ConfigureAwait(false);

                    endpointGroupArn = response.EndpointGroup.EndpointGroupArn;

                    await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {endpointGroupArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AddEndpoints",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.AddEndpoints\nclient.AddEndpointsAsync(new AddEndpointsRequest {{ EndpointGroupArn = \"{endpointGroupArn}\", EndpointConfigurations = [{{ EndpointId = \"{EndpointId}\", Weight = 10 }}] }})",
                async () =>
                {
                    AddEndpointsResponse response = await client.AddEndpointsAsync(
                        new AddEndpointsRequest
                        {
                            EndpointGroupArn = endpointGroupArn,
                            EndpointConfigurations = [new EndpointConfiguration { EndpointId = EndpointId, Weight = 10 }],
                        }, ct).ConfigureAwait(false);

                    await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join("; ", (response.EndpointDescriptions ?? []).Select(e => $"{e.EndpointId} weight {e.Weight} {e.HealthState}"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateEndpointGroup",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.UpdateEndpointGroup\nclient.UpdateEndpointGroupAsync(new UpdateEndpointGroupRequest {{ EndpointGroupArn = \"{endpointGroupArn}\", TrafficDialPercentage = 25, ThresholdCount = 5 }})",
                async () =>
                {
                    UpdateEndpointGroupResponse response = await client.UpdateEndpointGroupAsync(
                        new UpdateEndpointGroupRequest { EndpointGroupArn = endpointGroupArn, TrafficDialPercentage = 25, ThresholdCount = 5 }, ct).ConfigureAwait(false);

                    await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — dial {response.EndpointGroup.TrafficDialPercentage}%, threshold {response.EndpointGroup.ThresholdCount}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeEndpointGroup",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.DescribeEndpointGroup\nclient.DescribeEndpointGroupAsync(new DescribeEndpointGroupRequest {{ EndpointGroupArn = \"{endpointGroupArn}\" }})",
                async () =>
                {
                    DescribeEndpointGroupResponse response = await client.DescribeEndpointGroupAsync(new DescribeEndpointGroupRequest { EndpointGroupArn = endpointGroupArn }, ct).ConfigureAwait(false);
                    EndpointGroup group = response.EndpointGroup;
                    List<EndpointDescription> endpoints = group.EndpointDescriptions ?? [];

                    // Everything the steps above wrote has to read back from the one description.
                    if (group.TrafficDialPercentage != 25
                        || group.ThresholdCount != 5
                        || endpoints.Find(e => e.EndpointId == EndpointId)?.Weight != 10)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected dial 25, threshold 5 and {EndpointId} at weight 10 but got dial {group.TrafficDialPercentage}, threshold {group.ThresholdCount}, {endpoints.Count} endpoint(s).");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {group.EndpointGroupRegion}, dial {group.TrafficDialPercentage}%, health check {group.HealthCheckProtocol}:{group.HealthCheckPort}, {endpoints.Count} endpoint(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "TagResource",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.TagResource\nclient.TagResourceAsync(new TagResourceRequest {{ ResourceArn = \"{acceleratorArn}\", Tags = [episode=global-accelerator] }})",
                async () =>
                {
                    TagResourceResponse response = await client.TagResourceAsync(
                        new TagResourceRequest { ResourceArn = acceleratorArn, Tags = [new Tag { Key = "episode", Value = "global-accelerator" }] }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListTagsForResource",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.ListTagsForResource\nclient.ListTagsForResourceAsync(new ListTagsForResourceRequest {{ ResourceArn = \"{acceleratorArn}\" }})",
                async () =>
                {
                    ListTagsForResourceResponse response = await client.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = acceleratorArn }, ct).ConfigureAwait(false);
                    List<Tag> tags = response.Tags ?? [];

                    // One tag from CreateAccelerator and one from TagResource: both routes end up in the same set.
                    if (tags.Find(t => t.Key == "lab")?.Value != "flocilab" || tags.Find(t => t.Key == "episode")?.Value != "global-accelerator")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected lab=flocilab and episode=global-accelerator but got: {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}";
                }).ConfigureAwait(false);

            // The two refusals are the point of the steps, not a failure of them: Global Accelerator
            // makes deletion a deliberate, bottom-up act, and a page that only ever showed the happy
            // path would hide the rule that bites first in real use.
            yield return await RunStepAsync(
                "DeleteAccelerator — refused while enabled",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.DeleteAccelerator\nclient.DeleteAcceleratorAsync(new DeleteAcceleratorRequest {{ AcceleratorArn = \"{acceleratorArn}\" }})   // expect AcceleratorNotDisabledException",
                async () =>
                {
                    try
                    {
                        await client.DeleteAcceleratorAsync(new DeleteAcceleratorRequest { AcceleratorArn = acceleratorArn }, ct).ConfigureAwait(false);
                    }
                    // Either refusal is the rule working; which is checked first is the service's business.
                    catch (Exception ex) when (ex is AcceleratorNotDisabledException or AssociatedListenerFoundException)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("the accelerator was deleted although it is enabled and has a listener.");
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteListener — refused while it has an endpoint group",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.DeleteListener\nclient.DeleteListenerAsync(new DeleteListenerRequest {{ ListenerArn = \"{listenerArn}\" }})   // expect AssociatedEndpointGroupFoundException",
                async () =>
                {
                    try
                    {
                        await client.DeleteListenerAsync(new DeleteListenerRequest { ListenerArn = listenerArn }, ct).ConfigureAwait(false);
                    }
                    catch (AssociatedEndpointGroupFoundException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("the listener was deleted although it still has an endpoint group.");
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "RemoveEndpoints",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.RemoveEndpoints\nclient.RemoveEndpointsAsync(new RemoveEndpointsRequest {{ EndpointGroupArn = \"{endpointGroupArn}\", EndpointIdentifiers = [{{ EndpointId = \"{EndpointId}\" }}] }})",
                async () =>
                {
                    RemoveEndpointsResponse response = await client.RemoveEndpointsAsync(
                        new RemoveEndpointsRequest
                        {
                            EndpointGroupArn = endpointGroupArn,
                            EndpointIdentifiers = [new EndpointIdentifier { EndpointId = EndpointId }],
                        }, ct).ConfigureAwait(false);

                    await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteEndpointGroup",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.DeleteEndpointGroup\nclient.DeleteEndpointGroupAsync(new DeleteEndpointGroupRequest {{ EndpointGroupArn = \"{endpointGroupArn}\" }})",
                async () =>
                {
                    DeleteEndpointGroupResponse response = await client.DeleteEndpointGroupAsync(new DeleteEndpointGroupRequest { EndpointGroupArn = endpointGroupArn }, ct).ConfigureAwait(false);

                    await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteListener",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.DeleteListener\nclient.DeleteListenerAsync(new DeleteListenerRequest {{ ListenerArn = \"{listenerArn}\" }})",
                async () =>
                {
                    DeleteListenerResponse response = await client.DeleteListenerAsync(new DeleteListenerRequest { ListenerArn = listenerArn }, ct).ConfigureAwait(false);

                    await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateAccelerator — disable",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.UpdateAccelerator\nclient.UpdateAcceleratorAsync(new UpdateAcceleratorRequest {{ AcceleratorArn = \"{acceleratorArn}\", Enabled = false }})",
                async () =>
                {
                    UpdateAcceleratorResponse response = await client.UpdateAcceleratorAsync(new UpdateAcceleratorRequest { AcceleratorArn = acceleratorArn, Enabled = false }, ct).ConfigureAwait(false);

                    // Disabled is not deletable: real Global Accelerator holds the accelerator
                    // IN_PROGRESS for minutes after this call and refuses the delete until it is
                    // DEPLOYED again (the INSYNC corollary, plan §14). floci is DEPLOYED at once.
                    Accelerator accelerator = await WaitForDeployedAsync(client, acceleratorArn, ct).ConfigureAwait(false);

                    if (accelerator.Enabled != false)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected the accelerator disabled but it reads enabled {accelerator.Enabled}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — enabled {accelerator.Enabled}, {accelerator.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteAccelerator",
                $"POST {url}/\nX-Amz-Target: GlobalAccelerator_V20180706.DeleteAccelerator\nclient.DeleteAcceleratorAsync(new DeleteAcceleratorRequest {{ AcceleratorArn = \"{acceleratorArn}\" }})",
                async () =>
                {
                    DeleteAcceleratorResponse response = await client.DeleteAcceleratorAsync(new DeleteAcceleratorRequest { AcceleratorArn = acceleratorArn }, ct).ConfigureAwait(false);

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
                cleanup.Add(await this.DeleteAcceleratorTreeAsync(client, name, ct).ConfigureAwait(false));
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
    /// the emulator does something real Global Accelerator would not.
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
    /// Polls until the accelerator is <c>DEPLOYED</c>. Every change to an accelerator or anything
    /// under it — listeners and endpoint groups included — leaves it <c>IN_PROGRESS</c> on real AWS
    /// while the anycast network converges, and the next change is refused with
    /// <c>TransactionInProgressException</c> until it settles. So every mutation waits.
    /// </summary>
    private static async Task<Accelerator> WaitForDeployedAsync(IAmazonGlobalAccelerator client, string acceleratorArn, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            DescribeAcceleratorResponse response = await client.DescribeAcceleratorAsync(new DescribeAcceleratorRequest { AcceleratorArn = acceleratorArn }, ct).ConfigureAwait(false);

            if (response.Accelerator.Status == AcceleratorStatus.DEPLOYED)
            {
                return response.Accelerator;
            }

            if (Stopwatch.GetElapsedTime(started) > DeployTimeout)
            {
                throw new TimeoutException($"{acceleratorArn} was still {response.Accelerator.Status} after {DeployTimeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(DeployPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cleanup uses <see cref="CancellationToken.None"/> — a run that was cancelled still has an
    /// accelerator to remove. The ARN is minted by the server, so a create whose response was lost
    /// leaves nothing to delete by; the run's unique name is what finds it. Removal is bottom-up
    /// because Global Accelerator refuses anything else: endpoint groups, then listeners, then the
    /// accelerator once it is disabled and deployed.
    /// </summary>
    private async Task<DemoStep> DeleteAcceleratorTreeAsync(IAmazonGlobalAccelerator client, string name, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nListAccelerators, ListListeners, ListEndpointGroups, then Delete* bottom-up\nclient.DeleteAcceleratorAsync(new DeleteAcceleratorRequest {{ AcceleratorArn = <the accelerator named \"{name}\"> }})";

        return await RunStepAsync("DeleteAccelerator — cleanup", request, async () =>
        {
            Accelerator? accelerator = await FindAcceleratorAsync(client, name).ConfigureAwait(false);

            if (accelerator is null)
            {
                // Not a failure: the create never landed, and the server says so.
                return $"No accelerator named {name} exists — nothing to remove.";
            }

            string? listenerToken = null;

            do
            {
                ListListenersResponse listeners = await client.ListListenersAsync(new ListListenersRequest { AcceleratorArn = accelerator.AcceleratorArn, NextToken = listenerToken }, CancellationToken.None).ConfigureAwait(false);

                foreach (Listener listener in listeners.Listeners ?? [])
                {
                    ListEndpointGroupsResponse groups = await client.ListEndpointGroupsAsync(new ListEndpointGroupsRequest { ListenerArn = listener.ListenerArn }, CancellationToken.None).ConfigureAwait(false);

                    foreach (EndpointGroup group in groups.EndpointGroups ?? [])
                    {
                        await client.DeleteEndpointGroupAsync(new DeleteEndpointGroupRequest { EndpointGroupArn = group.EndpointGroupArn }, CancellationToken.None).ConfigureAwait(false);
                        await WaitForDeployedAsync(client, accelerator.AcceleratorArn, CancellationToken.None).ConfigureAwait(false);
                    }

                    await client.DeleteListenerAsync(new DeleteListenerRequest { ListenerArn = listener.ListenerArn }, CancellationToken.None).ConfigureAwait(false);
                    await WaitForDeployedAsync(client, accelerator.AcceleratorArn, CancellationToken.None).ConfigureAwait(false);
                }

                listenerToken = listeners.NextToken;
            }
            while (listenerToken is not null);

            if (accelerator.Enabled == true)
            {
                await client.UpdateAcceleratorAsync(new UpdateAcceleratorRequest { AcceleratorArn = accelerator.AcceleratorArn, Enabled = false }, CancellationToken.None).ConfigureAwait(false);
            }

            await WaitForDeployedAsync(client, accelerator.AcceleratorArn, CancellationToken.None).ConfigureAwait(false);

            DeleteAcceleratorResponse response = await client.DeleteAcceleratorAsync(new DeleteAcceleratorRequest { AcceleratorArn = accelerator.AcceleratorArn }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — deleted"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    // The run's accelerator can be on any page of a real account, so this follows NextToken
    // rather than reading the first page (plan §14, the Cloud Map row).
    private static async Task<Accelerator?> FindAcceleratorAsync(IAmazonGlobalAccelerator client, string name)
    {
        string? token = null;

        do
        {
            ListAcceleratorsResponse page = await client.ListAcceleratorsAsync(new ListAcceleratorsRequest { NextToken = token }, CancellationToken.None).ConfigureAwait(false);
            Accelerator? match = (page.Accelerators ?? []).Find(a => a.Name == name);

            if (match is not null)
            {
                return match;
            }

            token = page.NextToken;
        }
        while (token is not null);

        return null;
    }
}
