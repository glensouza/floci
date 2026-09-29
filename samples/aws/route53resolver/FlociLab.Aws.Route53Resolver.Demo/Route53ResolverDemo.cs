using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Route53Resolver;
using Amazon.Route53Resolver.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Route53Resolver;

/// <summary>
/// Route 53 Resolver rules, VPC associations and DNS Firewall domain lists against floci. Ordinary
/// AWSSDK.Route53Resolver code — the only emulator-aware line in the sample is in
/// <see cref="Route53ResolverClientFactory"/>.
/// </summary>
public sealed class Route53ResolverDemo(Route53ResolverClientFactory factory) : IServiceDemo
{
    // Resolver endpoints are left out on purpose: floci rejects the SDK's CreateResolverEndpoint
    // (plan §14), so the rule is a SYSTEM rule, which needs no endpoint on real AWS either. A
    // FORWARD rule would need one, and floci accepts a made-up ResolverEndpointId where real AWS
    // would not — a sample that leaned on that would only ever work on the emulator.
    //
    // The VPC is a placeholder floci does not validate. Real AWS needs a VPC that exists in the
    // region, so against a real account the association step fails red with the SDK's own message.
    private const string VpcId = "vpc-0123456789abcdef0";

    // .test is reserved (RFC 2606): the rule can never shadow a real domain when the page is
    // pointed at a real account.
    private const string RuleSuffix = ".flocilab.test";

    // Real AWS disassociates asynchronously — the association sits in DELETING for a while, and
    // deleting the rule before it goes answers ResourceInUseException. floci removes it at once,
    // so against the emulator the poll returns on its first call. Sized for real AWS; running out
    // of time is a failure, not a pass.
    private static readonly TimeSpan DisassociationPollBudget = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DisassociationPollInterval = TimeSpan.FromSeconds(2);

    public string Provider => CloudProvider.Aws;

    public string Slug => "route53resolver";

    public string DisplayName => "Route 53 Resolver";

    public string Category => "Networking";

    public string Route => "/aws/route53resolver";

    /// <summary>
    /// ListFirewallDomainLists capped at one. Not ListResolverRules: a fresh account has no rules,
    /// and an empty list is exactly the shape that trips AWSSDK collection handling (plan §14, the
    /// IAM row). The AWS-managed domain lists are always present, so this never reads empty.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonRoute53Resolver client = factory.Create();
            ListFirewallDomainListsResponse response = await client.ListFirewallDomainListsAsync(new ListFirewallDomainListsRequest { MaxResults = 1 }, ct).ConfigureAwait(false);
            int count = response.FirewallDomainLists?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListFirewallDomainLists returned {count} list(s) on the first page.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonRoute53Resolver client = factory.Create();

        // Unique per run, so two runs never collide and a rule left by a crashed run never makes
        // the next one fail. CreatorRequestId is the idempotency token, so it is per run too.
        string run = Guid.NewGuid().ToString("N")[..12];
        string domainName = $"corp-{run}{RuleSuffix}";
        string ruleName = $"flocilab-{run}";
        string renamed = $"flocilab-{run}-renamed";
        string listName = $"flocilab-{run}-blocklist";

        string? ruleId = null;
        string? listId = null;
        bool associated = false;

        // A second rule minted by a broken idempotency check, removed in the finally alongside
        // the first rather than inside the step, where a failed delete would mask the finding.
        List<string> strayRuleIds = [];

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "ListResolverRules — before",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.ListResolverRules\nclient.ListResolverRulesAsync(new ListResolverRulesRequest())",
                async () =>
                {
                    ListResolverRulesResponse response = await client.ListResolverRulesAsync(new ListResolverRulesRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ResolverRules?.Count ?? 0} rule(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateResolverRule",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.CreateResolverRule\nclient.CreateResolverRuleAsync(new CreateResolverRuleRequest {{ CreatorRequestId = \"{run}\", Name = \"{ruleName}\", RuleType = SYSTEM, DomainName = \"{domainName}\" }})",
                async () =>
                {
                    CreateResolverRuleResponse response = await client.CreateResolverRuleAsync(
                        new CreateResolverRuleRequest { CreatorRequestId = run, Name = ruleName, RuleType = RuleTypeOption.SYSTEM, DomainName = domainName }, ct).ConfigureAwait(false);

                    // Claimed as soon as the response names it: cleanup keys off this id, and an
                    // id that never arrived means there is no rule this run can find to remove.
                    ruleId = response.ResolverRule.Id;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ResolverRule.Id} {response.ResolverRule.RuleType} {response.ResolverRule.DomainName}, status {response.ResolverRule.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateResolverRule — repeated CreatorRequestId",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.CreateResolverRule\nclient.CreateResolverRuleAsync(new CreateResolverRuleRequest {{ CreatorRequestId = \"{run}\", Name = \"{ruleName}-again\", RuleType = SYSTEM, DomainName = \"{domainName}\" }})",
                async () =>
                {
                    // CreatorRequestId is the idempotency token. Replaying it with a different
                    // Name must be refused rather than minting a second rule, so the refusal is
                    // the passing outcome here and a second rule is the failure.
                    try
                    {
                        CreateResolverRuleResponse response = await client.CreateResolverRuleAsync(
                            new CreateResolverRuleRequest { CreatorRequestId = run, Name = $"{ruleName}-again", RuleType = RuleTypeOption.SYSTEM, DomainName = domainName }, ct).ConfigureAwait(false);

                        // Handing back the original rule is the other way a backend can answer a
                        // replay. That rule is this run's own, so it must not be deleted here —
                        // report the mismatch and leave the rule to the steps that follow.
                        if (response.ResolverRule.Id == ruleId)
                        {
                            throw new InvalidOperationException(
                                $"HTTP {(int)response.HttpStatusCode} — the original rule ({ruleId}) came back for a repeated CreatorRequestId with a different Name; real Route 53 Resolver refuses it.");
                        }

                        strayRuleIds.Add(response.ResolverRule.Id);

                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — a second rule ({response.ResolverRule.Id}) was created for a repeated CreatorRequestId.");
                    }
                    catch (ResourceExistsException ex)
                    {
                        return $"HTTP {(int)ex.StatusCode} — {ex.ErrorCode}: {ex.Message}\n(refused, as real Route 53 Resolver does)";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetResolverRule",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.GetResolverRule\nclient.GetResolverRuleAsync(new GetResolverRuleRequest {{ ResolverRuleId = \"{ruleId}\" }})",
                async () =>
                {
                    GetResolverRuleResponse response = await client.GetResolverRuleAsync(new GetResolverRuleRequest { ResolverRuleId = ruleId }, ct).ConfigureAwait(false);

                    if (response.ResolverRule.DomainName != domainName)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected {domainName} but the rule says {response.ResolverRule.DomainName}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ResolverRule.Name}, {response.ResolverRule.DomainName}, owner {response.ResolverRule.OwnerId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateResolverRule",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.UpdateResolverRule\nclient.UpdateResolverRuleAsync(new UpdateResolverRuleRequest {{ ResolverRuleId = \"{ruleId}\", Config = {{ Name = \"{renamed}\" }} }})",
                async () =>
                {
                    UpdateResolverRuleResponse response = await client.UpdateResolverRuleAsync(
                        new UpdateResolverRuleRequest { ResolverRuleId = ruleId, Config = new ResolverRuleConfig { Name = renamed } }, ct).ConfigureAwait(false);

                    // A rename that did not stick did not round-trip; say so rather than show a
                    // green badge over the old name.
                    if (response.ResolverRule.Name != renamed)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — the rule is still called {response.ResolverRule.Name}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — renamed to {response.ResolverRule.Name}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AssociateResolverRule",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.AssociateResolverRule\nclient.AssociateResolverRuleAsync(new AssociateResolverRuleRequest {{ ResolverRuleId = \"{ruleId}\", VPCId = \"{VpcId}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the association exists and the rule cannot be deleted on
                    // real AWS until it goes.
                    associated = true;
                    AssociateResolverRuleResponse response = await client.AssociateResolverRuleAsync(
                        new AssociateResolverRuleRequest { ResolverRuleId = ruleId, VPCId = VpcId }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ResolverRuleAssociation.Id} {response.ResolverRuleAssociation.VPCId}, status {response.ResolverRuleAssociation.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListResolverRuleAssociations",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.ListResolverRuleAssociations\nclient.ListResolverRuleAssociationsAsync(new ListResolverRuleAssociationsRequest {{ Filters = [{{ ResolverRuleId = \"{ruleId}\" }}] }})",
                async () =>
                {
                    ListResolverRuleAssociationsResponse response = await client.ListResolverRuleAssociationsAsync(
                        new ListResolverRuleAssociationsRequest { Filters = [new Filter { Name = "ResolverRuleId", Values = [ruleId!] }] }, ct).ConfigureAwait(false);

                    List<ResolverRuleAssociation> found = response.ResolverRuleAssociations ?? [];

                    if (!found.Exists(a => a.ResolverRuleId == ruleId && a.VPCId == VpcId))
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — no association of {ruleId} with {VpcId}. Found {found.Count}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {found.Count} association(s): {string.Join(", ", found.Select(a => $"{a.Id} → {a.VPCId}"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateFirewallDomainList",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.CreateFirewallDomainList\nclient.CreateFirewallDomainListAsync(new CreateFirewallDomainListRequest {{ CreatorRequestId = \"{run}\", Name = \"{listName}\" }})",
                async () =>
                {
                    CreateFirewallDomainListResponse response = await client.CreateFirewallDomainListAsync(
                        new CreateFirewallDomainListRequest { CreatorRequestId = run, Name = listName }, ct).ConfigureAwait(false);

                    listId = response.FirewallDomainList.Id;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.FirewallDomainList.Id}, {response.FirewallDomainList.DomainCount} domain(s), status {response.FirewallDomainList.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetFirewallDomainList",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.GetFirewallDomainList\nclient.GetFirewallDomainListAsync(new GetFirewallDomainListRequest {{ FirewallDomainListId = \"{listId}\" }})",
                async () =>
                {
                    GetFirewallDomainListResponse response = await client.GetFirewallDomainListAsync(
                        new GetFirewallDomainListRequest { FirewallDomainListId = listId }, ct).ConfigureAwait(false);

                    if (response.FirewallDomainList.Name != listName)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected {listName} but the list says {response.FirewallDomainList.Name}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {response.FirewallDomainList.Name}, {response.FirewallDomainList.DomainCount} domain(s), status {response.FirewallDomainList.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DisassociateResolverRule",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.DisassociateResolverRule\nclient.DisassociateResolverRuleAsync(new DisassociateResolverRuleRequest {{ ResolverRuleId = \"{ruleId}\", VPCId = \"{VpcId}\" }})",
                async () =>
                {
                    DisassociateResolverRuleResponse response = await client.DisassociateResolverRuleAsync(
                        new DisassociateResolverRuleRequest { ResolverRuleId = ruleId, VPCId = VpcId }, ct).ConfigureAwait(false);

                    string waited = await WaitForDisassociationAsync(client, ruleId!, ct).ConfigureAwait(false);
                    associated = false;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ResolverRuleAssociation.Id} status {response.ResolverRuleAssociation.Status}\n{waited}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The steps it produces are yielded
            // below — an iterator may not yield from inside a finally. Order matters on real AWS:
            // a rule cannot be deleted while a VPC is still associated with it.
            if (ruleId is not null && associated)
            {
                cleanup.Add(await this.DisassociateAsync(client, ruleId, ct).ConfigureAwait(false));
            }

            if (ruleId is not null)
            {
                cleanup.Add(await this.DeleteRuleAsync(client, ruleId, ct).ConfigureAwait(false));
            }

            foreach (string strayRuleId in strayRuleIds)
            {
                cleanup.Add(await this.DeleteRuleAsync(client, strayRuleId, ct).ConfigureAwait(false));
            }

            if (listId is not null)
            {
                cleanup.Add(await this.DeleteListAsync(client, listId, ct).ConfigureAwait(false));
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

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Route 53 Resolver would not.
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
    /// Polls until no association of <paramref name="ruleId"/> with the VPC is listed, so the rule
    /// delete that follows is not refused on real AWS. Throws when the budget runs out.
    /// </summary>
    private static async Task<string> WaitForDisassociationAsync(IAmazonRoute53Resolver client, string ruleId, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        ListResolverRuleAssociationsRequest request = new() { Filters = [new Filter { Name = "ResolverRuleId", Values = [ruleId] }] };

        for (int polls = 1; ; polls++)
        {
            ListResolverRuleAssociationsResponse response = await client.ListResolverRuleAssociationsAsync(request, ct).ConfigureAwait(false);
            List<ResolverRuleAssociation> remaining = (response.ResolverRuleAssociations ?? []).FindAll(a => a.VPCId == VpcId);

            if (remaining.Count == 0)
            {
                return $"association gone after {polls} poll(s), {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s";
            }

            if (Stopwatch.GetElapsedTime(started) >= DisassociationPollBudget)
            {
                throw new TimeoutException($"the association is still {remaining[0].Status} after {DisassociationPollBudget.TotalSeconds:0} s; the rule cannot be deleted until it goes.");
            }

            await Task.Delay(DisassociationPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Cleanup calls use <see cref="CancellationToken.None"/> — a run that was cancelled still has
    /// an association to remove. An association that was never made answers
    /// <see cref="ResourceNotFoundException"/>, which is a clean run finishing, not a failure.
    /// </summary>
    private async Task<DemoStep> DisassociateAsync(IAmazonRoute53Resolver client, string ruleId, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.DisassociateResolverRule\nclient.DisassociateResolverRuleAsync(new DisassociateResolverRuleRequest {{ ResolverRuleId = \"{ruleId}\", VPCId = \"{VpcId}\" }})";

        return await RunStepAsync("DisassociateResolverRule — cleanup", request, async () =>
        {
            try
            {
                DisassociateResolverRuleResponse response = await client.DisassociateResolverRuleAsync(
                    new DisassociateResolverRuleRequest { ResolverRuleId = ruleId, VPCId = VpcId }, CancellationToken.None).ConfigureAwait(false);
                string waited = await WaitForDisassociationAsync(client, ruleId, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — removed the association, {waited}"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (ResourceNotFoundException)
            {
                return "Nothing to remove — the association was never made.";
            }
        }).ConfigureAwait(false);
    }

    private async Task<DemoStep> DeleteRuleAsync(IAmazonRoute53Resolver client, string ruleId, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.DeleteResolverRule\nclient.DeleteResolverRuleAsync(new DeleteResolverRuleRequest {{ ResolverRuleId = \"{ruleId}\" }})";

        return await RunStepAsync("DeleteResolverRule — cleanup", request, async () =>
        {
            try
            {
                DeleteResolverRuleResponse response = await client.DeleteResolverRuleAsync(
                    new DeleteResolverRuleRequest { ResolverRuleId = ruleId }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — {response.ResolverRule.Id} {response.ResolverRule.Status}"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (ResourceNotFoundException)
            {
                return "Nothing to remove — the rule was never created.";
            }
        }).ConfigureAwait(false);
    }

    private async Task<DemoStep> DeleteListAsync(IAmazonRoute53Resolver client, string listId, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nX-Amz-Target: Route53Resolver.DeleteFirewallDomainList\nclient.DeleteFirewallDomainListAsync(new DeleteFirewallDomainListRequest {{ FirewallDomainListId = \"{listId}\" }})";

        return await RunStepAsync("DeleteFirewallDomainList — cleanup", request, async () =>
        {
            try
            {
                DeleteFirewallDomainListResponse response = await client.DeleteFirewallDomainListAsync(
                    new DeleteFirewallDomainListRequest { FirewallDomainListId = listId }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — {response.FirewallDomainList.Id} {response.FirewallDomainList.Status}"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (ResourceNotFoundException)
            {
                return "Nothing to remove — the list was never created.";
            }
        }).ConfigureAwait(false);
    }
}
