using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.SSOAdmin;
using Amazon.SSOAdmin.Model;
using FlociLab.Core;

namespace FlociLab.Aws.IdentityCenter;

/// <summary>
/// A permission set with a managed policy, an inline policy and tags, provisioned, refused as a
/// duplicate, then deleted and confirmed gone, against floci. Ordinary AWSSDK.SSOAdmin code — the
/// only emulator-aware line in the sample is in <see cref="IdentityCenterClientFactory"/>.
/// </summary>
public sealed class IdentityCenterDemo(IdentityCenterClientFactory factory) : IServiceDemo
{
    private const string ManagedPolicyArn = "arn:aws:iam::aws:policy/ReadOnlyAccess";

    private const string InlinePolicy = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":"s3:ListAllMyBuckets","Resource":"*"}]}""";

    private static readonly TimeSpan ProvisionTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ProvisionPollInterval = TimeSpan.FromSeconds(2);

    public string Provider => CloudProvider.Aws;

    public string Slug => "identitycenter";

    public string DisplayName => "IAM Identity Center";

    public string Category => "Identity and access";

    public string Route => "/aws/identitycenter";

    /// <summary>
    /// ListInstances. Floci ships one instance, <c>floci-identity-center</c>, so a fresh container
    /// answers with a count of one; real AWS answers zero until Identity Center is enabled, which
    /// is still an Ok probe — the service answered.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonSSOAdmin client = factory.Create();
            ListInstancesResponse response = await client.ListInstancesAsync(new ListInstancesRequest(), ct).ConfigureAwait(false);
            int count = response.Instances?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListInstances returned {count} instance(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonSSOAdmin client = factory.Create();

        // Unique per run, so two runs never collide, and — because the ARN is minted by the server —
        // the one handle cleanup still has if a create landed while its response was lost.
        string name = $"flocilab-ps-{Guid.NewGuid().ToString("N")[..12]}";
        string url = factory.ServiceUrl;
        string target = $"POST {url}/\nX-Amz-Target: SWBExternalService";

        string instanceArn = string.Empty;
        string permissionSetArn = string.Empty;

        bool createAttempted = false;
        bool deleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep instanceStep = await RunStepAsync(
                "ListInstances",
                $"{target}.ListInstances\nclient.ListInstancesAsync(new ListInstancesRequest())",
                async () =>
                {
                    ListInstancesResponse response = await client.ListInstancesAsync(new ListInstancesRequest(), ct).ConfigureAwait(false);
                    InstanceMetadata? instance = response.Instances?.FirstOrDefault();

                    if (instance is null)
                    {
                        throw new InvalidOperationException("no Identity Center instance exists; enable one before running this sample against real AWS.");
                    }

                    instanceArn = instance.InstanceArn;

                    return $"HTTP {(int)response.HttpStatusCode} — {instance.InstanceArn}, identity store {instance.IdentityStoreId}, {instance.Status}";
                }).ConfigureAwait(false);

            yield return instanceStep;

            // Every later call needs the instance ARN. Without one — real AWS with Identity Center
            // not enabled — twelve more red steps would only bury the one real cause.
            if (!instanceStep.Succeeded)
            {
                yield break;
            }

            DemoStep createStep = await RunStepAsync(
                "CreatePermissionSet",
                $"{target}.CreatePermissionSet\nclient.CreatePermissionSetAsync(new CreatePermissionSetRequest {{ InstanceArn = \"{instanceArn}\", Name = \"{name}\", Description = \"created by FlociLab\", SessionDuration = \"PT2H\", Tags = [lab=flocilab] }})",
                async () =>
                {
                    // Claimed before the call, not after: a request that lands while its response is
                    // lost (the page's Dispose cancels mid-flight) still created a permission set.
                    // Cleanup asks the server, by name, rather than trusting this flag.
                    createAttempted = true;

                    CreatePermissionSetResponse response = await client.CreatePermissionSetAsync(
                        new CreatePermissionSetRequest
                        {
                            InstanceArn = instanceArn,
                            Name = name,
                            Description = "created by FlociLab",
                            SessionDuration = "PT2H",
                            Tags = [new Tag { Key = "lab", Value = "flocilab" }],
                        }, ct).ConfigureAwait(false);

                    permissionSetArn = response.PermissionSet.PermissionSetArn;

                    return $"HTTP {(int)response.HttpStatusCode} — {permissionSetArn}, session {response.PermissionSet.SessionDuration}";
                }).ConfigureAwait(false);

            yield return createStep;

            // With no permission set, the duplicate check would pass for the wrong reason and every
            // later step would run against an empty ARN. The finally still looks for one by name.
            if (!createStep.Succeeded)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "CreatePermissionSet — refused as a duplicate",
                $"{target}.CreatePermissionSet\nclient.CreatePermissionSetAsync(new CreatePermissionSetRequest {{ InstanceArn = \"{instanceArn}\", Name = \"{name}\" }})   // expect ConflictException",
                async () =>
                {
                    CreatePermissionSetResponse duplicate;

                    try
                    {
                        duplicate = await client.CreatePermissionSetAsync(new CreatePermissionSetRequest { InstanceArn = instanceArn, Name = name }, ct).ConfigureAwait(false);
                    }
                    // The refusal is the point of the step, not a failure of it.
                    catch (ConflictException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    // Accepted where it should have been refused. Remove it here: the delete below and
                    // the cleanup by name each account for one permission set, not two.
                    string duplicateArn = duplicate.PermissionSet.PermissionSetArn;
                    await client.DeletePermissionSetAsync(new DeletePermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = duplicateArn }, CancellationToken.None).ConfigureAwait(false);

                    throw new InvalidOperationException($"a second permission set named {name} was created ({duplicateArn}, since deleted).");
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AttachManagedPolicyToPermissionSet",
                $"{target}.AttachManagedPolicyToPermissionSet\nclient.AttachManagedPolicyToPermissionSetAsync(new AttachManagedPolicyToPermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\", ManagedPolicyArn = \"{ManagedPolicyArn}\" }})",
                async () =>
                {
                    AttachManagedPolicyToPermissionSetResponse response = await client.AttachManagedPolicyToPermissionSetAsync(
                        new AttachManagedPolicyToPermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn, ManagedPolicyArn = ManagedPolicyArn }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutInlinePolicyToPermissionSet",
                $"{target}.PutInlinePolicyToPermissionSet\nclient.PutInlinePolicyToPermissionSetAsync(new PutInlinePolicyToPermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\", InlinePolicy = <s3:ListAllMyBuckets> }})",
                async () =>
                {
                    PutInlinePolicyToPermissionSetResponse response = await client.PutInlinePolicyToPermissionSetAsync(
                        new PutInlinePolicyToPermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn, InlinePolicy = InlinePolicy }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdatePermissionSet",
                $"{target}.UpdatePermissionSet\nclient.UpdatePermissionSetAsync(new UpdatePermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\", Description = \"updated by FlociLab\", SessionDuration = \"PT4H\" }})",
                async () =>
                {
                    UpdatePermissionSetResponse response = await client.UpdatePermissionSetAsync(
                        new UpdatePermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn, Description = "updated by FlociLab", SessionDuration = "PT4H" }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribePermissionSet — the update reads back",
                $"{target}.DescribePermissionSet\nclient.DescribePermissionSetAsync(new DescribePermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\" }})",
                async () =>
                {
                    DescribePermissionSetResponse response = await client.DescribePermissionSetAsync(
                        new DescribePermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn }, ct).ConfigureAwait(false);
                    PermissionSet set = response.PermissionSet;

                    if (set.Name != name || set.SessionDuration != "PT4H" || set.Description != "updated by FlociLab")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected {name}, PT4H and the updated description but got {set.Name}, {set.SessionDuration}, \"{set.Description}\".");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {set.Name}, session {set.SessionDuration}, \"{set.Description}\"";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListManagedPoliciesInPermissionSet and GetInlinePolicyForPermissionSet",
                $"{target}.ListManagedPoliciesInPermissionSet\nclient.ListManagedPoliciesInPermissionSetAsync(new ListManagedPoliciesInPermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\" }})\nclient.GetInlinePolicyForPermissionSetAsync(new GetInlinePolicyForPermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\" }})",
                async () =>
                {
                    ListManagedPoliciesInPermissionSetResponse managed = await client.ListManagedPoliciesInPermissionSetAsync(
                        new ListManagedPoliciesInPermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn }, ct).ConfigureAwait(false);
                    GetInlinePolicyForPermissionSetResponse inline = await client.GetInlinePolicyForPermissionSetAsync(
                        new GetInlinePolicyForPermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn }, ct).ConfigureAwait(false);

                    List<AttachedManagedPolicy> attached = managed.AttachedManagedPolicies ?? [];

                    if (attached.Find(p => p.Arn == ManagedPolicyArn) is null || !inline.InlinePolicy.Contains("s3:ListAllMyBuckets", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"expected {ManagedPolicyArn} and the s3:ListAllMyBuckets inline policy but got {attached.Count} managed policy(ies) and inline policy \"{inline.InlinePolicy}\".");
                    }

                    return $"HTTP {(int)managed.HttpStatusCode} — {string.Join(", ", attached.Select(p => p.Name))}; inline policy {inline.InlinePolicy.Length} chars";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "TagResource",
                $"{target}.TagResource\nclient.TagResourceAsync(new TagResourceRequest {{ InstanceArn = \"{instanceArn}\", ResourceArn = \"{permissionSetArn}\", Tags = [episode=identity-center] }})",
                async () =>
                {
                    TagResourceResponse response = await client.TagResourceAsync(
                        new TagResourceRequest { InstanceArn = instanceArn, ResourceArn = permissionSetArn, Tags = [new Tag { Key = "episode", Value = "identity-center" }] }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListTagsForResource",
                $"{target}.ListTagsForResource\nclient.ListTagsForResourceAsync(new ListTagsForResourceRequest {{ InstanceArn = \"{instanceArn}\", ResourceArn = \"{permissionSetArn}\" }})",
                async () =>
                {
                    ListTagsForResourceResponse response = await client.ListTagsForResourceAsync(
                        new ListTagsForResourceRequest { InstanceArn = instanceArn, ResourceArn = permissionSetArn }, ct).ConfigureAwait(false);
                    List<Tag> tags = response.Tags ?? [];

                    // One tag from CreatePermissionSet and one from TagResource: both routes end up in the same set.
                    if (tags.Find(t => t.Key == "lab")?.Value != "flocilab" || tags.Find(t => t.Key == "episode")?.Value != "identity-center")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected lab=flocilab and episode=identity-center but got: {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ProvisionPermissionSet",
                $"{target}.ProvisionPermissionSet\nclient.ProvisionPermissionSetAsync(new ProvisionPermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\", TargetType = ALL_PROVISIONED_ACCOUNTS }})\nclient.DescribePermissionSetProvisioningStatusAsync(...)   // until SUCCEEDED",
                async () =>
                {
                    // ALL_PROVISIONED_ACCOUNTS rather than AWS_ACCOUNT: it needs no account id, and on a
                    // permission set nobody is assigned to it is a safe no-op against a real organization.
                    ProvisionPermissionSetResponse response = await client.ProvisionPermissionSetAsync(
                        new ProvisionPermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn, TargetType = ProvisionTargetType.ALL_PROVISIONED_ACCOUNTS }, ct).ConfigureAwait(false);

                    PermissionSetProvisioningStatus status = await WaitForProvisionedAsync(client, instanceArn, response.PermissionSetProvisioningStatus, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — request {status.RequestId}, {status.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeletePermissionSet",
                $"{target}.DeletePermissionSet\nclient.DeletePermissionSetAsync(new DeletePermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\" }})",
                async () =>
                {
                    DeletePermissionSetResponse response = await client.DeletePermissionSetAsync(
                        new DeletePermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn }, ct).ConfigureAwait(false);

                    deleted = true;

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribePermissionSet — refused once deleted",
                $"{target}.DescribePermissionSet\nclient.DescribePermissionSetAsync(new DescribePermissionSetRequest {{ InstanceArn = \"{instanceArn}\", PermissionSetArn = \"{permissionSetArn}\" }})   // expect ResourceNotFoundException",
                async () =>
                {
                    try
                    {
                        await client.DescribePermissionSetAsync(new DescribePermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = permissionSetArn }, ct).ConfigureAwait(false);
                    }
                    catch (ResourceNotFoundException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("the permission set can still be described after DeletePermissionSet.");
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean instance. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            if (createAttempted && !deleted)
            {
                cleanup.Add(await this.DeletePermissionSetByNameAsync(client, instanceArn, name, ct).ConfigureAwait(false));
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
    /// the emulator does something real Identity Center would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes what exists.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// Provisioning is asynchronous on real AWS: the call answers <c>IN_PROGRESS</c> and the
    /// result is read back with the request id. floci answers <c>SUCCEEDED</c> at once, so the loop
    /// is a single iteration there.
    /// </summary>
    private static async Task<PermissionSetProvisioningStatus> WaitForProvisionedAsync(IAmazonSSOAdmin client, string instanceArn, PermissionSetProvisioningStatus status, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        while (status.Status == StatusValues.IN_PROGRESS)
        {
            if (Stopwatch.GetElapsedTime(started) > ProvisionTimeout)
            {
                throw new TimeoutException($"provisioning request {status.RequestId} was still {status.Status} after {ProvisionTimeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(ProvisionPollInterval, ct).ConfigureAwait(false);

            DescribePermissionSetProvisioningStatusResponse response = await client.DescribePermissionSetProvisioningStatusAsync(
                new DescribePermissionSetProvisioningStatusRequest { InstanceArn = instanceArn, ProvisionPermissionSetRequestId = status.RequestId }, ct).ConfigureAwait(false);

            status = response.PermissionSetProvisioningStatus;
        }

        if (status.Status == StatusValues.FAILED)
        {
            throw new InvalidOperationException($"provisioning request {status.RequestId} failed: {status.FailureReason}");
        }

        return status;
    }

    /// <summary>
    /// Cleanup uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// permission set to remove. The ARN is minted by the server, so a create whose response was
    /// lost leaves nothing to delete by; the run's unique name is what finds it. There is no
    /// lookup by name, so this pages <c>ListPermissionSets</c> and describes each ARN.
    /// </summary>
    private async Task<DemoStep> DeletePermissionSetByNameAsync(IAmazonSSOAdmin client, string instanceArn, string name, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nListPermissionSets, DescribePermissionSet, then DeletePermissionSet\nclient.DeletePermissionSetAsync(new DeletePermissionSetRequest {{ PermissionSetArn = <the permission set named \"{name}\"> }})";

        return await RunStepAsync("DeletePermissionSet — cleanup", request, async () =>
        {
            // The run failed before it learned the instance, so nothing was created.
            if (instanceArn.Length == 0)
            {
                return "No instance was resolved — nothing to remove.";
            }

            string? token = null;

            do
            {
                ListPermissionSetsResponse page = await client.ListPermissionSetsAsync(new ListPermissionSetsRequest { InstanceArn = instanceArn, NextToken = token }, CancellationToken.None).ConfigureAwait(false);

                foreach (string arn in page.PermissionSets ?? [])
                {
                    DescribePermissionSetResponse described;

                    try
                    {
                        described = await client.DescribePermissionSetAsync(new DescribePermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = arn }, CancellationToken.None).ConfigureAwait(false);
                    }
                    // The instance is shared, so another run can delete its own set between this
                    // page of the list and the describe. That set is not ours; keep looking.
                    catch (ResourceNotFoundException)
                    {
                        continue;
                    }

                    if (described.PermissionSet.Name != name)
                    {
                        continue;
                    }

                    DeletePermissionSetResponse response = await client.DeletePermissionSetAsync(new DeletePermissionSetRequest { InstanceArn = instanceArn, PermissionSetArn = arn }, CancellationToken.None).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — deleted {arn}"
                        + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
                }

                token = page.NextToken;
            }
            while (token is not null);

            // Not a failure: the create never landed, and the server says so.
            return $"No permission set named {name} exists — nothing to remove.";
        }).ConfigureAwait(false);
    }
}
