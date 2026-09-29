using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.ElasticLoadBalancingV2;
using Amazon.ElasticLoadBalancingV2.Model;
using Amazon.Runtime;
using FlociLab.Core;
using ElbAction = Amazon.ElasticLoadBalancingV2.Model.Action;

namespace FlociLab.Aws.ElbV2;

/// <summary>
/// An application load balancer with a target group, a listener, a listener rule, attributes and
/// tags against floci. Ordinary AWSSDK.ElasticLoadBalancingV2 code — the only emulator-aware line in
/// the sample is in <see cref="ElbV2ClientFactory"/>.
/// </summary>
public sealed class ElbV2Demo(ElbV2ClientFactory factory) : IServiceDemo
{
    // A private (RFC 1918) address. An ip-type target group accepts one outside the VPC's own CIDR
    // (floci's default VPC is 172.31.0.0/16), as real AWS does for on-premises targets, so the
    // sample needs no instance and no second service.
    private const string TargetAddress = "10.0.0.10";

    private const int TargetPort = 8080;

    private const string IdleTimeoutKey = "idle_timeout.timeout_seconds";

    public string Provider => CloudProvider.Aws;

    public string Slug => "elbv2";

    public string DisplayName => "ELB v2";

    public string Category => "Networking";

    public string Route => "/aws/elbv2";

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
            using IAmazonElasticLoadBalancingV2 client = factory.Create();
            DescribeLoadBalancersResponse response = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest { PageSize = 1 }, ct).ConfigureAwait(false);
            int count = response.LoadBalancers?.Count ?? 0;

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
        using IAmazonElasticLoadBalancingV2 client = factory.Create();

        // Unique per run, so two runs never collide and a load balancer left by a crashed run never
        // makes the next one fail with DuplicateLoadBalancerName. Load balancer and target group
        // names share a 32-character limit, which 12 hex digits stays well inside.
        string run = Guid.NewGuid().ToString("N")[..12];
        string tgName = $"flocilab-tg-{run}";
        string lbName = $"flocilab-lb-{run}";
        string url = factory.ServiceUrl;

        string? targetGroupArn = null;
        string? loadBalancerArn = null;
        string? listenerArn = null;
        bool targetGroupDeleted = false;
        bool loadBalancerDeleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "DescribeLoadBalancers — before",
                $"POST {url}/\nAction=DescribeLoadBalancers\nclient.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest())",
                async () =>
                {
                    DescribeLoadBalancersResponse response = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.LoadBalancers?.Count ?? 0} load balancer(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateTargetGroup",
                $"POST {url}/\nAction=CreateTargetGroup\nclient.CreateTargetGroupAsync(new CreateTargetGroupRequest {{ Name = \"{tgName}\", Protocol = HTTP, Port = {TargetPort}, VpcId = \"{factory.VpcId}\", TargetType = ip, HealthCheckPath = \"/health\" }})",
                async () =>
                {
                    CreateTargetGroupResponse response = await client.CreateTargetGroupAsync(
                        new CreateTargetGroupRequest
                        {
                            Name = tgName,
                            Protocol = ProtocolEnum.HTTP,
                            Port = TargetPort,
                            VpcId = factory.VpcId,
                            TargetType = TargetTypeEnum.Ip,
                            HealthCheckPath = "/health",
                        }, ct).ConfigureAwait(false);

                    // Claimed as soon as the response names it: cleanup keys off this ARN.
                    TargetGroup created = response.TargetGroups[0];
                    targetGroupArn = created.TargetGroupArn;

                    return $"HTTP {(int)response.HttpStatusCode} — {created.TargetGroupName}, {created.Protocol}:{created.Port}, health check {created.HealthCheckPath}\n{created.TargetGroupArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateLoadBalancer",
                $"POST {url}/\nAction=CreateLoadBalancer\nclient.CreateLoadBalancerAsync(new CreateLoadBalancerRequest {{ Name = \"{lbName}\", Type = application, Scheme = internet-facing, Subnets = [{string.Join(", ", factory.SubnetIds)}], Tags = [lab=flocilab] }})",
                async () =>
                {
                    CreateLoadBalancerResponse response = await client.CreateLoadBalancerAsync(
                        new CreateLoadBalancerRequest
                        {
                            Name = lbName,
                            Type = LoadBalancerTypeEnum.Application,
                            Scheme = LoadBalancerSchemeEnum.InternetFacing,
                            Subnets = [.. factory.SubnetIds],
                            Tags = [new Tag { Key = "lab", Value = "flocilab" }],
                        }, ct).ConfigureAwait(false);

                    LoadBalancer created = response.LoadBalancers[0];
                    loadBalancerArn = created.LoadBalancerArn;

                    // The state is reported, not asserted: real AWS answers "provisioning" and takes
                    // minutes to reach "active", and nothing below needs it to have.
                    return $"HTTP {(int)response.HttpStatusCode} — {created.LoadBalancerName} {created.State?.Code}, {created.DNSName}, zones {string.Join(", ", (created.AvailabilityZones ?? []).Select(z => z.ZoneName))}\n{created.LoadBalancerArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "RegisterTargets",
                $"POST {url}/\nAction=RegisterTargets\nclient.RegisterTargetsAsync(new RegisterTargetsRequest {{ TargetGroupArn = \"{targetGroupArn}\", Targets = [{TargetAddress}:{TargetPort}] }})",
                async () =>
                {
                    RegisterTargetsResponse response = await client.RegisterTargetsAsync(
                        new RegisterTargetsRequest
                        {
                            TargetGroupArn = targetGroupArn,
                            Targets = [new TargetDescription { Id = TargetAddress, Port = TargetPort }],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeTargetHealth",
                $"POST {url}/\nAction=DescribeTargetHealth\nclient.DescribeTargetHealthAsync(new DescribeTargetHealthRequest {{ TargetGroupArn = \"{targetGroupArn}\" }})",
                async () =>
                {
                    DescribeTargetHealthResponse response = await client.DescribeTargetHealthAsync(new DescribeTargetHealthRequest { TargetGroupArn = targetGroupArn }, ct).ConfigureAwait(false);
                    List<TargetHealthDescription> targets = response.TargetHealthDescriptions ?? [];

                    // The state is reported, not asserted. No process listens on the address, so a
                    // real target would go unhealthy once its checks ran; what this proves is that
                    // the registration is visible.
                    if (!targets.Exists(t => t.Target.Id == TargetAddress))
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — {TargetAddress} was registered but the target group lists {targets.Count} target(s).");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join("; ", targets.Select(t => $"{t.Target.Id}:{t.Target.Port} {t.TargetHealth?.State} ({t.TargetHealth?.Reason})"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateListener",
                $"POST {url}/\nAction=CreateListener\nclient.CreateListenerAsync(new CreateListenerRequest {{ LoadBalancerArn = \"{loadBalancerArn}\", Protocol = HTTP, Port = 80, DefaultActions = [forward -> {tgName}] }})",
                async () =>
                {
                    CreateListenerResponse response = await client.CreateListenerAsync(
                        new CreateListenerRequest
                        {
                            LoadBalancerArn = loadBalancerArn,
                            Protocol = ProtocolEnum.HTTP,
                            Port = 80,
                            DefaultActions = [new ElbAction { Type = ActionTypeEnum.Forward, TargetGroupArn = targetGroupArn }],
                        }, ct).ConfigureAwait(false);

                    listenerArn = response.Listeners[0].ListenerArn;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Listeners[0].Protocol}:{response.Listeners[0].Port}\n{listenerArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateRule",
                $"POST {url}/\nAction=CreateRule\nclient.CreateRuleAsync(new CreateRuleRequest {{ ListenerArn = \"{listenerArn}\", Priority = 10, Conditions = [path-pattern /api/*], Actions = [fixed-response 200] }})",
                async () =>
                {
                    CreateRuleResponse response = await client.CreateRuleAsync(
                        new CreateRuleRequest
                        {
                            ListenerArn = listenerArn,
                            Priority = 10,
                            Conditions = [new RuleCondition { Field = "path-pattern", Values = ["/api/*"] }],
                            Actions =
                            [
                                new ElbAction
                                {
                                    Type = ActionTypeEnum.FixedResponse,
                                    FixedResponseConfig = new FixedResponseActionConfig { StatusCode = "200", ContentType = "text/plain", MessageBody = "served by the listener rule" },
                                },
                            ],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — priority {response.Rules[0].Priority}\n{response.Rules[0].RuleArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeRules",
                $"POST {url}/\nAction=DescribeRules\nclient.DescribeRulesAsync(new DescribeRulesRequest {{ ListenerArn = \"{listenerArn}\" }})",
                async () =>
                {
                    DescribeRulesResponse response = await client.DescribeRulesAsync(new DescribeRulesRequest { ListenerArn = listenerArn }, ct).ConfigureAwait(false);
                    List<Rule> rules = response.Rules ?? [];

                    // A listener always carries its default rule, so the one this run added makes two.
                    if (rules.Count != 2 || !rules.Exists(r => r.IsDefault == true) || !rules.Exists(r => r.Priority == "10"))
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected the default rule and priority 10 but got: {string.Join(", ", rules.Select(r => r.Priority))}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", rules.Select(r => r.IsDefault == true ? "default" : $"priority {r.Priority}"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ModifyLoadBalancerAttributes",
                $"POST {url}/\nAction=ModifyLoadBalancerAttributes\nclient.ModifyLoadBalancerAttributesAsync(new ModifyLoadBalancerAttributesRequest {{ LoadBalancerArn = \"{loadBalancerArn}\", Attributes = [{IdleTimeoutKey} = 120] }})",
                async () =>
                {
                    ModifyLoadBalancerAttributesResponse response = await client.ModifyLoadBalancerAttributesAsync(
                        new ModifyLoadBalancerAttributesRequest
                        {
                            LoadBalancerArn = loadBalancerArn,
                            Attributes = [new LoadBalancerAttribute { Key = IdleTimeoutKey, Value = "120" }],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeLoadBalancerAttributes",
                $"POST {url}/\nAction=DescribeLoadBalancerAttributes\nclient.DescribeLoadBalancerAttributesAsync(new DescribeLoadBalancerAttributesRequest {{ LoadBalancerArn = \"{loadBalancerArn}\" }})",
                async () =>
                {
                    DescribeLoadBalancerAttributesResponse response = await client.DescribeLoadBalancerAttributesAsync(new DescribeLoadBalancerAttributesRequest { LoadBalancerArn = loadBalancerArn }, ct).ConfigureAwait(false);
                    LoadBalancerAttribute? idle = (response.Attributes ?? []).Find(a => a.Key == IdleTimeoutKey);

                    if (idle?.Value != "120")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected {IdleTimeoutKey} = 120 but the load balancer says {idle?.Value ?? "nothing"}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {IdleTimeoutKey} = {idle.Value} (of {response.Attributes?.Count ?? 0} attributes)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AddTags",
                $"POST {url}/\nAction=AddTags\nclient.AddTagsAsync(new AddTagsRequest {{ ResourceArns = [\"{loadBalancerArn}\"], Tags = [episode=elbv2] }})",
                async () =>
                {
                    AddTagsResponse response = await client.AddTagsAsync(
                        new AddTagsRequest { ResourceArns = [loadBalancerArn], Tags = [new Tag { Key = "episode", Value = "elbv2" }] }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeTags",
                $"POST {url}/\nAction=DescribeTags\nclient.DescribeTagsAsync(new DescribeTagsRequest {{ ResourceArns = [\"{loadBalancerArn}\"] }})",
                async () =>
                {
                    DescribeTagsResponse response = await client.DescribeTagsAsync(new DescribeTagsRequest { ResourceArns = [loadBalancerArn] }, ct).ConfigureAwait(false);
                    List<Tag> tags = response.TagDescriptions?.FirstOrDefault()?.Tags ?? [];

                    // One tag from CreateLoadBalancer and one from AddTags: both routes end up in the same set.
                    if (tags.Find(t => t.Key == "lab")?.Value != "flocilab" || tags.Find(t => t.Key == "episode")?.Value != "elbv2")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected lab=flocilab and episode=elbv2 but got: {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteTargetGroup — while in use",
                $"POST {url}/\nAction=DeleteTargetGroup\nclient.DeleteTargetGroupAsync(new DeleteTargetGroupRequest {{ TargetGroupArn = \"{targetGroupArn}\" }})",
                async () =>
                {
                    // A target group a listener still forwards to must not be deletable, so the
                    // refusal is the passing outcome.
                    try
                    {
                        DeleteTargetGroupResponse response = await client.DeleteTargetGroupAsync(new DeleteTargetGroupRequest { TargetGroupArn = targetGroupArn }, ct).ConfigureAwait(false);

                        targetGroupDeleted = true;

                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — a target group with a listener pointing at it was deleted; real Elastic Load Balancing refuses it.");
                    }
                    catch (ResourceInUseException ex)
                    {
                        return $"HTTP {(int)ex.StatusCode} — {ex.ErrorCode}: {ex.Message}\n(refused, as real Elastic Load Balancing does)";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeregisterTargets",
                $"POST {url}/\nAction=DeregisterTargets\nclient.DeregisterTargetsAsync(new DeregisterTargetsRequest {{ TargetGroupArn = \"{targetGroupArn}\", Targets = [{TargetAddress}:{TargetPort}] }})",
                async () =>
                {
                    DeregisterTargetsResponse response = await client.DeregisterTargetsAsync(
                        new DeregisterTargetsRequest
                        {
                            TargetGroupArn = targetGroupArn,
                            Targets = [new TargetDescription { Id = TargetAddress, Port = TargetPort }],
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            // The load balancer goes first: its listeners are what pin the target group, and
            // deleting a load balancer removes them with it.
            yield return await RunStepAsync(
                "DeleteLoadBalancer",
                $"POST {url}/\nAction=DeleteLoadBalancer\nclient.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest {{ LoadBalancerArn = \"{loadBalancerArn}\" }})",
                async () =>
                {
                    DeleteLoadBalancerResponse response = await client.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest { LoadBalancerArn = loadBalancerArn }, ct).ConfigureAwait(false);

                    loadBalancerDeleted = true;

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteTargetGroup",
                $"POST {url}/\nAction=DeleteTargetGroup\nclient.DeleteTargetGroupAsync(new DeleteTargetGroupRequest {{ TargetGroupArn = \"{targetGroupArn}\" }})",
                async () =>
                {
                    DeleteTargetGroupResponse response = await DeleteTargetGroupWhenFreeAsync(client, targetGroupArn, ct).ConfigureAwait(false);

                    targetGroupDeleted = true;

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The steps it produces are yielded
            // below — an iterator may not yield from inside a finally. Order matters: the
            // load balancer has to be gone before the target group its listener uses can be.
            if (loadBalancerArn is not null && !loadBalancerDeleted)
            {
                cleanup.Add(await this.DeleteLoadBalancerAsync(client, loadBalancerArn, ct).ConfigureAwait(false));
            }

            if (targetGroupArn is not null && !targetGroupDeleted)
            {
                cleanup.Add(await this.DeleteTargetGroupAsync(client, targetGroupArn, ct).ConfigureAwait(false));
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
    /// Deletes a target group, retrying while it is still in use. Real Elastic Load Balancing
    /// removes a deleted load balancer's listeners in the background, so for a few seconds after
    /// DeleteLoadBalancer returns the target group is still refused with ResourceInUse; floci
    /// removes them at once, so against the emulator the loop never waits. The 60 s budget is
    /// sized for real AWS, which is the case it exists for.
    /// </summary>
    private static async Task<DeleteTargetGroupResponse> DeleteTargetGroupWhenFreeAsync(IAmazonElasticLoadBalancingV2 client, string? arn, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await client.DeleteTargetGroupAsync(new DeleteTargetGroupRequest { TargetGroupArn = arn }, ct).ConfigureAwait(false);
            }
            catch (ResourceInUseException) when (attempt < 12)
            {
                // Still pinned by a listener that is being removed; the last attempt's refusal propagates.
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cleanup calls use <see cref="CancellationToken.None"/> — a run that was cancelled still has
    /// a load balancer to remove. One that was never made, or is already gone, answers
    /// <see cref="LoadBalancerNotFoundException"/>, which is a clean run finishing, not a failure.
    /// </summary>
    private async Task<DemoStep> DeleteLoadBalancerAsync(IAmazonElasticLoadBalancingV2 client, string arn, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nAction=DeleteLoadBalancer\nclient.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest {{ LoadBalancerArn = \"{arn}\" }})";

        return await RunStepAsync("DeleteLoadBalancer — cleanup", request, async () =>
        {
            try
            {
                DeleteLoadBalancerResponse response = await client.DeleteLoadBalancerAsync(new DeleteLoadBalancerRequest { LoadBalancerArn = arn }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — deleted"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (LoadBalancerNotFoundException)
            {
                return "Nothing to remove — the load balancer was never created.";
            }
        }).ConfigureAwait(false);
    }

    private async Task<DemoStep> DeleteTargetGroupAsync(IAmazonElasticLoadBalancingV2 client, string arn, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nAction=DeleteTargetGroup\nclient.DeleteTargetGroupAsync(new DeleteTargetGroupRequest {{ TargetGroupArn = \"{arn}\" }})";

        return await RunStepAsync("DeleteTargetGroup — cleanup", request, async () =>
        {
            try
            {
                DeleteTargetGroupResponse response = await DeleteTargetGroupWhenFreeAsync(client, arn, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — deleted"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (TargetGroupNotFoundException)
            {
                return "Nothing to remove — the target group was never created.";
            }
        }).ConfigureAwait(false);
    }
}
