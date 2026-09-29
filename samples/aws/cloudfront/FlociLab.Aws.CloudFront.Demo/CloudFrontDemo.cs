using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.CloudFront;

/// <summary>
/// A CloudFront distribution with an S3 origin, its origin access control, an invalidation and
/// tags against floci. Ordinary AWSSDK.CloudFront code — the only emulator-aware line in the
/// sample is in <see cref="CloudFrontClientFactory"/>.
/// </summary>
public sealed class CloudFrontDemo(CloudFrontClientFactory factory) : IServiceDemo
{
    // Real CloudFront takes minutes to leave InProgress, and it will not delete a distribution
    // until it is disabled *and* Deployed again. floci reports Deployed straight away, so against
    // the emulator every poll below returns on its first call. Sized for real AWS; running out of
    // time is a failure, not a pass.
    private static readonly TimeSpan DeployPollBudget = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan DeployPollInterval = TimeSpan.FromSeconds(15);

    // The S3 origin is a bucket name nothing owns: CloudFront checks the domain's shape, not that
    // the bucket exists, so the sample needs no second service and no second SDK package.
    private const string OriginDomainSuffix = ".s3.amazonaws.com";

    // The AWS-managed CachingOptimized policy. A production distribution names a cache policy
    // rather than the legacy ForwardedValues/MinTTL pair, and the managed ids are the same in
    // every account.
    private const string CachePolicyId = "658327ea-f89d-4fab-a63d-7e88639e58f6";

    public string Provider => CloudProvider.Aws;

    public string Slug => "cloudfront";

    public string DisplayName => "CloudFront";

    public string Category => "Networking";

    public string Route => "/aws/cloudfront";

    /// <summary>
    /// ListDistributions capped at one. A fresh account lists nothing, and an empty list is
    /// exactly the shape that trips AWSSDK collection handling (plan §14, the IAM row), so the
    /// count is read through null-conditionals and zero is a perfectly good answer.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonCloudFront client = factory.Create();
            ListDistributionsResponse response = await client.ListDistributionsAsync(new ListDistributionsRequest { MaxItems = "1" }, ct).ConfigureAwait(false);
            int count = response.DistributionList?.Items?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListDistributions returned {count} distribution(s) on the first page.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonCloudFront client = factory.Create();

        // Unique per run, so two runs never collide and a distribution left by a crashed run never
        // makes the next one fail. CallerReference is the idempotency token, so it is per run too.
        string run = Guid.NewGuid().ToString("N")[..12];
        string comment = $"flocilab-{run}";
        string renamed = $"flocilab-{run}-renamed";
        string originDomain = $"flocilab-{run}{OriginDomainSuffix}";

        string? oacId = null;
        string? distributionId = null;
        string? distributionArn = null;
        bool distributionDeleted = false;
        bool oacDeleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "ListDistributions — before",
                $"GET {factory.ServiceUrl}/2020-05-31/distribution\nclient.ListDistributionsAsync(new ListDistributionsRequest())",
                async () =>
                {
                    ListDistributionsResponse response = await client.ListDistributionsAsync(new ListDistributionsRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.DistributionList?.Items?.Count ?? 0} distribution(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateOriginAccessControl",
                $"POST {factory.ServiceUrl}/2020-05-31/origin-access-control\nclient.CreateOriginAccessControlAsync(new CreateOriginAccessControlRequest {{ OriginAccessControlConfig = {{ Name = \"{comment}\", SigningProtocol = sigv4, SigningBehavior = always, OriginAccessControlOriginType = s3 }} }})",
                async () =>
                {
                    CreateOriginAccessControlResponse response = await client.CreateOriginAccessControlAsync(
                        new CreateOriginAccessControlRequest
                        {
                            OriginAccessControlConfig = new OriginAccessControlConfig
                            {
                                Name = comment,
                                Description = "FlociLab round-trip",
                                SigningProtocol = OriginAccessControlSigningProtocols.Sigv4,
                                SigningBehavior = OriginAccessControlSigningBehaviors.Always,
                                OriginAccessControlOriginType = OriginAccessControlOriginTypes.S3,
                            },
                        }, ct).ConfigureAwait(false);

                    // Claimed as soon as the response names it: cleanup keys off this id.
                    oacId = response.OriginAccessControl.Id;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.OriginAccessControl.Id}, ETag {response.ETag}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateDistribution",
                $"POST {factory.ServiceUrl}/2020-05-31/distribution\nclient.CreateDistributionAsync(new CreateDistributionRequest {{ DistributionConfig = {{ CallerReference = \"{run}\", Comment = \"{comment}\", Enabled = true, Origins = [{{ DomainName = \"{originDomain}\", OriginAccessControlId = \"{oacId}\" }}] }} }})",
                async () =>
                {
                    CreateDistributionResponse response = await client.CreateDistributionAsync(
                        new CreateDistributionRequest { DistributionConfig = BuildConfig(run, comment, originDomain, oacId!, enabled: true) }, ct).ConfigureAwait(false);

                    distributionId = response.Distribution.Id;
                    distributionArn = response.Distribution.ARN;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Distribution.Id} {response.Distribution.Status}, {response.Distribution.DomainName}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetDistribution",
                $"GET {factory.ServiceUrl}/2020-05-31/distribution/{distributionId}\nclient.GetDistributionAsync(new GetDistributionRequest {{ Id = \"{distributionId}\" }})",
                async () =>
                {
                    GetDistributionResponse response = await WaitForDeployedAsync(client, distributionId!, ct).ConfigureAwait(false);

                    if (response.Distribution.DistributionConfig.Comment != comment)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected comment {comment} but the distribution says {response.Distribution.DistributionConfig.Comment}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Distribution.Status}, ETag {response.ETag}";
                }).ConfigureAwait(false);

            // The ETag from before the update is kept: it is the stale one the next-but-one step
            // replays.
            string? staleETag = null;

            yield return await RunStepAsync(
                "UpdateDistribution",
                $"PUT {factory.ServiceUrl}/2020-05-31/distribution/{distributionId}/config\nIf-Match: <ETag from GetDistributionConfig>\nclient.UpdateDistributionAsync(new UpdateDistributionRequest {{ Id = \"{distributionId}\", IfMatch = etag, DistributionConfig = {{ Comment = \"{renamed}\" }} }})",
                async () =>
                {
                    GetDistributionConfigResponse current = await client.GetDistributionConfigAsync(new GetDistributionConfigRequest { Id = distributionId }, ct).ConfigureAwait(false);
                    staleETag = current.ETag;

                    // The config that came back is edited and sent back whole — an update replaces
                    // the configuration, it does not patch it.
                    current.DistributionConfig.Comment = renamed;
                    UpdateDistributionResponse response = await client.UpdateDistributionAsync(
                        new UpdateDistributionRequest { Id = distributionId, IfMatch = current.ETag, DistributionConfig = current.DistributionConfig }, ct).ConfigureAwait(false);

                    // A rename that did not stick did not round-trip; say so rather than show a
                    // green badge over the old comment.
                    if (response.Distribution.DistributionConfig.Comment != renamed)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — the distribution is still commented {response.Distribution.DistributionConfig.Comment}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — comment is now {response.Distribution.DistributionConfig.Comment}, new ETag {response.ETag}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateDistribution — stale ETag",
                $"PUT {factory.ServiceUrl}/2020-05-31/distribution/{distributionId}/config\nIf-Match: {staleETag}\nclient.UpdateDistributionAsync(new UpdateDistributionRequest {{ Id = \"{distributionId}\", IfMatch = \"{staleETag}\", ... }})",
                async () =>
                {
                    // The If-Match ETag is optimistic locking: an update built on a
                    // configuration that has since changed must be refused, so the refusal is the
                    // passing outcome here and an accepted write is the failure.
                    GetDistributionConfigResponse current = await client.GetDistributionConfigAsync(new GetDistributionConfigRequest { Id = distributionId }, ct).ConfigureAwait(false);

                    try
                    {
                        UpdateDistributionResponse response = await client.UpdateDistributionAsync(
                            new UpdateDistributionRequest { Id = distributionId, IfMatch = staleETag, DistributionConfig = current.DistributionConfig }, ct).ConfigureAwait(false);

                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — an update built on a stale ETag was accepted; real CloudFront refuses it.");
                    }
                    // Real CloudFront answers a stale ETag with PreconditionFailed; floci's
                    // answer is InvalidIfMatchVersion. Both are the refusal this step is after.
                    catch (Exception ex) when (ex is PreconditionFailedException or InvalidIfMatchVersionException)
                    {
                        AmazonServiceException refusal = (AmazonServiceException)ex;

                        return $"HTTP {(int)refusal.StatusCode} — {refusal.ErrorCode}: {refusal.Message}\n(refused, as real CloudFront does)";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateInvalidation",
                $"POST {factory.ServiceUrl}/2020-05-31/distribution/{distributionId}/invalidation\nclient.CreateInvalidationAsync(new CreateInvalidationRequest {{ DistributionId = \"{distributionId}\", InvalidationBatch = {{ CallerReference = \"{run}\", Paths = [\"/*\"] }} }})",
                async () =>
                {
                    CreateInvalidationResponse response = await client.CreateInvalidationAsync(
                        new CreateInvalidationRequest
                        {
                            DistributionId = distributionId,
                            InvalidationBatch = new InvalidationBatch { CallerReference = run, Paths = new Paths { Quantity = 1, Items = ["/*"] } },
                        }, ct).ConfigureAwait(false);

                    // Real CloudFront answers InProgress and finishes in a minute or two; floci
                    // answers Completed. Either is a working invalidation, so the status is shown,
                    // not asserted.
                    return $"HTTP {(int)response.HttpStatusCode} — {response.Invalidation.Id} {response.Invalidation.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListInvalidations",
                $"GET {factory.ServiceUrl}/2020-05-31/distribution/{distributionId}/invalidation\nclient.ListInvalidationsAsync(new ListInvalidationsRequest {{ DistributionId = \"{distributionId}\" }})",
                async () =>
                {
                    ListInvalidationsResponse response = await client.ListInvalidationsAsync(new ListInvalidationsRequest { DistributionId = distributionId }, ct).ConfigureAwait(false);
                    int count = response.InvalidationList?.Items?.Count ?? 0;

                    if (count != 1)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected the one invalidation this run made but the list has {count}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {count} invalidation(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "TagResource",
                $"POST {factory.ServiceUrl}/2020-05-31/tagging?Operation=Tag&Resource={distributionArn}\nclient.TagResourceAsync(new TagResourceRequest {{ Resource = \"{distributionArn}\", Tags = [lab=flocilab] }})",
                async () =>
                {
                    TagResourceResponse response = await client.TagResourceAsync(
                        new TagResourceRequest { Resource = distributionArn, Tags = new Tags { Items = [new Tag { Key = "lab", Value = "flocilab" }] } }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListTagsForResource",
                $"GET {factory.ServiceUrl}/2020-05-31/tagging?Resource={distributionArn}\nclient.ListTagsForResourceAsync(new ListTagsForResourceRequest {{ Resource = \"{distributionArn}\" }})",
                async () =>
                {
                    ListTagsForResourceResponse response = await client.ListTagsForResourceAsync(new ListTagsForResourceRequest { Resource = distributionArn }, ct).ConfigureAwait(false);
                    Tag? tag = (response.Tags?.Items ?? []).Find(t => t.Key == "lab");

                    if (tag?.Value != "flocilab")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — the tag lab=flocilab did not come back.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — lab={tag.Value}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteDistribution — while enabled",
                $"DELETE {factory.ServiceUrl}/2020-05-31/distribution/{distributionId}\nclient.DeleteDistributionAsync(new DeleteDistributionRequest {{ Id = \"{distributionId}\", IfMatch = etag }})",
                async () =>
                {
                    // CloudFront makes deletion a two-step: disable, wait, then delete. Deleting an
                    // enabled distribution must be refused, so the refusal is the passing outcome.
                    GetDistributionResponse current = await client.GetDistributionAsync(new GetDistributionRequest { Id = distributionId }, ct).ConfigureAwait(false);

                    try
                    {
                        DeleteDistributionResponse response = await client.DeleteDistributionAsync(
                            new DeleteDistributionRequest { Id = distributionId, IfMatch = current.ETag }, ct).ConfigureAwait(false);

                        distributionDeleted = true;

                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — an enabled distribution was deleted; real CloudFront refuses it.");
                    }
                    catch (DistributionNotDisabledException ex)
                    {
                        return $"HTTP {(int)ex.StatusCode} — {ex.ErrorCode}: {ex.Message}\n(refused, as real CloudFront does)";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateDistribution — disable",
                $"PUT {factory.ServiceUrl}/2020-05-31/distribution/{distributionId}/config\nclient.UpdateDistributionAsync(new UpdateDistributionRequest {{ Id = \"{distributionId}\", IfMatch = etag, DistributionConfig = {{ Enabled = false }} }})",
                async () => await DisableAsync(client, distributionId!, ct).ConfigureAwait(false)).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteDistribution",
                $"DELETE {factory.ServiceUrl}/2020-05-31/distribution/{distributionId}\nclient.DeleteDistributionAsync(new DeleteDistributionRequest {{ Id = \"{distributionId}\", IfMatch = etag }})",
                async () =>
                {
                    GetDistributionResponse current = await client.GetDistributionAsync(new GetDistributionRequest { Id = distributionId }, ct).ConfigureAwait(false);
                    DeleteDistributionResponse response = await client.DeleteDistributionAsync(
                        new DeleteDistributionRequest { Id = distributionId, IfMatch = current.ETag }, ct).ConfigureAwait(false);

                    distributionDeleted = true;

                    return $"HTTP {(int)response.HttpStatusCode} — {distributionId} deleted";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteOriginAccessControl",
                $"DELETE {factory.ServiceUrl}/2020-05-31/origin-access-control/{oacId}\nclient.DeleteOriginAccessControlAsync(new DeleteOriginAccessControlRequest {{ Id = \"{oacId}\", IfMatch = etag }})",
                async () =>
                {
                    // Only after the distribution is gone: real CloudFront refuses to delete an
                    // origin access control that a distribution still uses.
                    GetOriginAccessControlResponse current = await client.GetOriginAccessControlAsync(new GetOriginAccessControlRequest { Id = oacId }, ct).ConfigureAwait(false);
                    DeleteOriginAccessControlResponse response = await client.DeleteOriginAccessControlAsync(
                        new DeleteOriginAccessControlRequest { Id = oacId, IfMatch = current.ETag }, ct).ConfigureAwait(false);

                    oacDeleted = true;

                    return $"HTTP {(int)response.HttpStatusCode} — {oacId} deleted";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The steps it produces are yielded
            // below — an iterator may not yield from inside a finally. Order matters on real AWS:
            // the distribution has to be disabled and gone before the origin access control it
            // uses can be deleted.
            if (distributionId is not null && !distributionDeleted)
            {
                cleanup.Add(await this.DeleteDistributionAsync(client, distributionId, ct).ConfigureAwait(false));
            }

            if (oacId is not null && !oacDeleted)
            {
                cleanup.Add(await this.DeleteOriginAccessControlAsync(client, oacId, ct).ConfigureAwait(false));
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

    private static DistributionConfig BuildConfig(string callerReference, string comment, string originDomain, string oacId, bool enabled)
        => new()
        {
            CallerReference = callerReference,
            Comment = comment,
            Enabled = enabled,
            Origins = new Origins
            {
                Quantity = 1,
                Items =
                [
                    new Origin
                    {
                        Id = "s3-origin",
                        DomainName = originDomain,
                        // An empty OriginAccessIdentity is how a distribution says "use the origin
                        // access control instead" — the legacy identity is the thing being replaced.
                        S3OriginConfig = new S3OriginConfig { OriginAccessIdentity = string.Empty },
                        OriginAccessControlId = oacId,
                    },
                ],
            },
            DefaultCacheBehavior = new DefaultCacheBehavior
            {
                TargetOriginId = "s3-origin",
                ViewerProtocolPolicy = ViewerProtocolPolicy.RedirectToHttps,
                CachePolicyId = CachePolicyId,
            },
        };

    /// <summary>
    /// Polls until the distribution reports <c>Deployed</c>, so the delete that follows is not
    /// refused on real AWS. Throws when the budget runs out.
    /// </summary>
    private static async Task<GetDistributionResponse> WaitForDeployedAsync(IAmazonCloudFront client, string id, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            GetDistributionResponse response = await client.GetDistributionAsync(new GetDistributionRequest { Id = id }, ct).ConfigureAwait(false);

            if (response.Distribution.Status == "Deployed")
            {
                return response;
            }

            if (Stopwatch.GetElapsedTime(started) >= DeployPollBudget)
            {
                throw new TimeoutException($"the distribution is still {response.Distribution.Status} after {DeployPollBudget.TotalMinutes:0} min; it cannot be deleted until it is Deployed.");
            }

            await Task.Delay(DeployPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disables the distribution and waits for the change to deploy. Already-disabled skips the
    /// update but still waits, which is what lets the cleanup path share it with the main flow.
    /// </summary>
    private static async Task<string> DisableAsync(IAmazonCloudFront client, string id, CancellationToken ct)
    {
        GetDistributionConfigResponse current = await client.GetDistributionConfigAsync(new GetDistributionConfigRequest { Id = id }, ct).ConfigureAwait(false);

        // Disabled is not the same as deletable: a run cancelled (or timed out) during the
        // disable step leaves the distribution disabled but still InProgress on real AWS, and a
        // delete then is refused with DistributionNotDisabled. floci is Deployed at once, so only
        // real AWS exercises this wait.
        if (current.DistributionConfig.Enabled != true)
        {
            GetDistributionResponse settled = await WaitForDeployedAsync(client, id, ct).ConfigureAwait(false);

            return $"Already disabled, {settled.Distribution.Status}.";
        }

        current.DistributionConfig.Enabled = false;
        UpdateDistributionResponse response = await client.UpdateDistributionAsync(
            new UpdateDistributionRequest { Id = id, IfMatch = current.ETag, DistributionConfig = current.DistributionConfig }, ct).ConfigureAwait(false);

        long started = Stopwatch.GetTimestamp();
        GetDistributionResponse deployed = await WaitForDeployedAsync(client, id, ct).ConfigureAwait(false);

        return $"HTTP {(int)response.HttpStatusCode} — disabled, {deployed.Distribution.Status} after {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s";
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real CloudFront would not.
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
    /// Cleanup calls use <see cref="CancellationToken.None"/> — a run that was cancelled still has
    /// a distribution to remove. A distribution that was never made, or is already gone, answers
    /// <see cref="NoSuchDistributionException"/>, which is a clean run finishing, not a failure.
    /// </summary>
    private async Task<DemoStep> DeleteDistributionAsync(IAmazonCloudFront client, string id, CancellationToken ct)
    {
        string request = $"DELETE {factory.ServiceUrl}/2020-05-31/distribution/{id}\nclient.DeleteDistributionAsync(new DeleteDistributionRequest {{ Id = \"{id}\", IfMatch = etag }})";

        return await RunStepAsync("DeleteDistribution — cleanup", request, async () =>
        {
            try
            {
                string disabled = await DisableAsync(client, id, CancellationToken.None).ConfigureAwait(false);
                GetDistributionResponse current = await client.GetDistributionAsync(new GetDistributionRequest { Id = id }, CancellationToken.None).ConfigureAwait(false);
                DeleteDistributionResponse response = await client.DeleteDistributionAsync(
                    new DeleteDistributionRequest { Id = id, IfMatch = current.ETag }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — {id} deleted ({disabled})"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (NoSuchDistributionException)
            {
                return "Nothing to remove — the distribution was never created.";
            }
        }).ConfigureAwait(false);
    }

    private async Task<DemoStep> DeleteOriginAccessControlAsync(IAmazonCloudFront client, string id, CancellationToken ct)
    {
        string request = $"DELETE {factory.ServiceUrl}/2020-05-31/origin-access-control/{id}\nclient.DeleteOriginAccessControlAsync(new DeleteOriginAccessControlRequest {{ Id = \"{id}\", IfMatch = etag }})";

        return await RunStepAsync("DeleteOriginAccessControl — cleanup", request, async () =>
        {
            try
            {
                GetOriginAccessControlResponse current = await client.GetOriginAccessControlAsync(new GetOriginAccessControlRequest { Id = id }, CancellationToken.None).ConfigureAwait(false);
                DeleteOriginAccessControlResponse response = await client.DeleteOriginAccessControlAsync(
                    new DeleteOriginAccessControlRequest { Id = id, IfMatch = current.ETag }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — {id} deleted"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (NoSuchOriginAccessControlException)
            {
                return "Nothing to remove — the origin access control was never created.";
            }
        }).ConfigureAwait(false);
    }
}
