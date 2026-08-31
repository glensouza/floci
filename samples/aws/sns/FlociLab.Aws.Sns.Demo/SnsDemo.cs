using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FlociLab.Core;

namespace FlociLab.Aws.Sns;

/// <summary>
/// Amazon SNS against floci. Ordinary AWSSDK.SimpleNotificationService code — the only
/// emulator-aware line in the sample is in <see cref="SnsClientFactory"/>.
/// </summary>
public sealed class SnsDemo(SnsClientFactory factory) : IServiceDemo
{
    private const string PublishedMessage = "Hello from FlociLab.";

    // SNS is a *query-protocol* service: form-urlencoded in, XML out, an Action parameter in the
    // body and no X-Amz-Target header anywhere. SQS — which this sample is otherwise shaped after —
    // is JSON-1.0 and does send X-Amz-Target, so the two look nothing alike on the wire despite the
    // SDK calls reading almost identically. Verified against floci: CreateTopic answers
    // Content-Type: application/xml with an xmlns of http://sns.amazonaws.com/doc/2010-03-31/,
    // which is where ApiVersion comes from.
    private const string FormEncoded = "Content-Type: application/x-www-form-urlencoded";

    private const string ApiVersion = "2010-03-31";

    // A real address is never sent to — an email subscription only ever reaches
    // "PendingConfirmation" until someone clicks the link AWS would have mailed. That is the
    // genuine SNS behaviour this step demonstrates, not a shortcut around it.
    private const string SubscriberEmail = "nobody@flocilab.example";

    public string Provider => CloudProvider.Aws;

    public string Slug => "sns";

    public string DisplayName => "SNS";

    public string Category => "Messaging";

    public string Route => "/aws/sns";

    /// <summary>ListTopics — one request, no state, and the cheapest call SNS has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonSimpleNotificationService client = factory.Create();
            ListTopicsResponse response = await client.ListTopicsAsync(new ListTopicsRequest(), ct).ConfigureAwait(false);
            int count = response.Topics?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListTopics returned {count} topic(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonSimpleNotificationService client = factory.Create();

        // Unique per run, so two runs never collide and a leftover topic from a crashed run never
        // makes the next one fail. SNS allows up to 256 chars of alphanumerics/hyphens/underscores.
        string topicName = $"flocilab-sns-{Guid.NewGuid():N}";
        bool created = false;
        string? topicArn = null;
        string? subscriptionArn = null;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListTopics — before",
                $"POST {factory.ServiceUrl}/\n{FormEncoded}\nAction=ListTopics&Version={ApiVersion}\nsns.ListTopicsAsync(new ListTopicsRequest())",
                async () =>
                {
                    ListTopicsResponse response = await client.ListTopicsAsync(new ListTopicsRequest(), ct).ConfigureAwait(false);
                    IEnumerable<string> arns = response.Topics?.Select(t => $"  {t.TopicArn}") ?? [];

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Topics?.Count ?? 0} topic(s)\n"
                        + string.Join('\n', arns);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateTopic",
                $"POST {factory.ServiceUrl}/\n{FormEncoded}\nAction=CreateTopic&Name={topicName}&Version={ApiVersion}\nsns.CreateTopicAsync(new CreateTopicRequest {{ Name = \"{topicName}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the topic exists and cleanup has to know about it. Cleanup
                    // treats an absent topic as a no-op, so claiming it early is free.
                    created = true;
                    CreateTopicResponse response = await client.CreateTopicAsync(
                        new CreateTopicRequest { Name = topicName }, ct).ConfigureAwait(false);
                    topicArn = response.TopicArn;

                    return $"HTTP {(int)response.HttpStatusCode} — TopicArn: {response.TopicArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Subscribe",
                $"POST {factory.ServiceUrl}/\n{FormEncoded}\nAction=Subscribe&TopicArn={topicArn}&Protocol=email&Endpoint={SubscriberEmail}&ReturnSubscriptionArn=true&Version={ApiVersion}\nsns.SubscribeAsync(new SubscribeRequest {{ TopicArn = \"{topicArn}\", Protocol = \"email\", Endpoint = \"{SubscriberEmail}\", ReturnSubscriptionArn = true }})",
                async () =>
                {
                    // CreateTopic's own step already reports why. This one still has to go out
                    // red: nothing was subscribed, and a green badge here would claim otherwise.
                    if (topicArn is null)
                    {
                        throw new InvalidOperationException("Skipped — CreateTopic did not return a topic ARN.");
                    }

                    // ReturnSubscriptionArn is not optional here, even though floci does not need
                    // it: real SNS returns the literal string "pending confirmation" instead of an
                    // ARN for an unconfirmed email subscription unless it is set, and Unsubscribe
                    // then fails with "An ARN must have at least 6 elements". floci hands back a
                    // real ARN either way (plan §14), so the emulator would never have caught this
                    // — only the real-AWS path this page also supports would.
                    SubscribeResponse response = await client.SubscribeAsync(
                        new SubscribeRequest { TopicArn = topicArn, Protocol = "email", Endpoint = SubscriberEmail, ReturnSubscriptionArn = true }, ct).ConfigureAwait(false);
                    subscriptionArn = response.SubscriptionArn;

                    return $"HTTP {(int)response.HttpStatusCode} — SubscriptionArn: {response.SubscriptionArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetTopicAttributes",
                $"POST {factory.ServiceUrl}/\n{FormEncoded}\nAction=GetTopicAttributes&TopicArn={topicArn}&Version={ApiVersion}\nsns.GetTopicAttributesAsync(new GetTopicAttributesRequest {{ TopicArn = \"{topicArn}\" }})",
                async () =>
                {
                    if (topicArn is null)
                    {
                        throw new InvalidOperationException("Skipped — CreateTopic did not return a topic ARN.");
                    }

                    GetTopicAttributesResponse response = await client.GetTopicAttributesAsync(
                        new GetTopicAttributesRequest { TopicArn = topicArn }, ct).ConfigureAwait(false);
                    // Null-conditional for the same reason response.Topics is guarded above: AWSSDK
                    // v4 leaves an unpopulated collection null rather than empty, so a response
                    // carrying no Attributes at all would NRE here instead of reporting what it saw.
                    string pending = response.Attributes?.GetValueOrDefault("SubscriptionsPending", "0") ?? "0";

                    // An email subscription that did not land as pending did not subscribe — real
                    // SNS holds it there until the confirmation link is clicked. A step that skips
                    // this check would show green for a subscribe that silently no-opped.
                    if (pending != "1")
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — SubscriptionsPending was \"{pending}\", expected \"1\".");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — SubscriptionsPending: {pending}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Publish",
                $"POST {factory.ServiceUrl}/\n{FormEncoded}\nAction=Publish&TopicArn={topicArn}&Message={PublishedMessage}&Version={ApiVersion}\nsns.PublishAsync(new PublishRequest {{ TopicArn = \"{topicArn}\", Message = \"{PublishedMessage}\" }})",
                async () =>
                {
                    if (topicArn is null)
                    {
                        throw new InvalidOperationException("Skipped — CreateTopic did not return a topic ARN.");
                    }

                    PublishResponse response = await client.PublishAsync(
                        new PublishRequest { TopicArn = topicArn, Message = PublishedMessage }, ct).ConfigureAwait(false);

                    // Fan-out to zero *confirmed* subscribers is not a failure — real SNS accepts
                    // and discards it exactly the same way. A MessageId is the only receipt SNS
                    // itself ever gives for that; there is no subscriber to poll for delivery,
                    // and a genuinely wired one belongs in a second SDK, which constraint 1 rules
                    // out for a single-package sample.
                    return string.IsNullOrEmpty(response.MessageId)
                        ? throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — no MessageId returned.")
                        : $"HTTP {(int)response.HttpStatusCode} — MessageId: {response.MessageId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Unsubscribe",
                $"POST {factory.ServiceUrl}/\n{FormEncoded}\nAction=Unsubscribe&SubscriptionArn={subscriptionArn}&Version={ApiVersion}\nsns.UnsubscribeAsync(new UnsubscribeRequest {{ SubscriptionArn = \"{subscriptionArn}\" }})",
                async () =>
                {
                    if (subscriptionArn is null)
                    {
                        throw new InvalidOperationException("Skipped — Subscribe did not return a subscription ARN.");
                    }

                    UnsubscribeResponse response = await client.UnsubscribeAsync(
                        new UnsubscribeRequest { SubscriptionArn = subscriptionArn }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await this.DeleteTopicAsync(client, topicName, ct).ConfigureAwait(false) : null;
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
    /// the emulator does something real SNS would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the topic.
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
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
    /// Resolves the topic's ARN by scanning <c>ListTopics</c> for one whose ARN ends in the
    /// generated name. SNS has no by-name lookup, and the obvious substitute — re-issuing
    /// <c>CreateTopic</c>, which SNS makes idempotent by name — is a *mutating* call: after a
    /// CreateTopic that failed for a non-transport reason it would create the very topic it is
    /// meant to remove and then report a green "removed the topic" for a run that created nothing.
    /// ListTopics keeps SQS's <c>GetQueueUrl</c> property of recovering a topic whose creation
    /// response never arrived, without that. The calls use <see cref="CancellationToken.None"/> —
    /// a run that was cancelled still has a topic to remove.
    /// </summary>
    private async Task<DemoStep> DeleteTopicAsync(IAmazonSimpleNotificationService client, string topicName, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\n{FormEncoded}\nAction=ListTopics&Version={ApiVersion}, then Action=DeleteTopic&TopicArn=…:{topicName}&Version={ApiVersion}\nsns.DeleteTopicAsync(the ARN from sns.ListTopicsAsync() ending in \"{topicName}\")";

        return await RunStepAsync("DeleteTopic — cleanup", request, async () =>
        {
            string? resolvedArn = await FindTopicArnAsync(client, topicName).ConfigureAwait(false);

            if (resolvedArn is null)
            {
                return "Nothing to remove — the topic was never created.";
            }

            DeleteTopicResponse response = await client.DeleteTopicAsync(
                new DeleteTopicRequest { TopicArn = resolvedArn }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — removed the topic"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Pages through ListTopics looking for the run's own topic. Paginated deliberately: floci
    /// returns everything in one response, but a real account with more than 100 topics would not,
    /// and cleanup that silently stopped at page one would leak a topic per run.
    /// </summary>
    private static async Task<string?> FindTopicArnAsync(IAmazonSimpleNotificationService client, string topicName)
    {
        string suffix = $":{topicName}";
        string? nextToken = null;

        do
        {
            ListTopicsResponse page = await client.ListTopicsAsync(
                new ListTopicsRequest { NextToken = nextToken }, CancellationToken.None).ConfigureAwait(false);

            string? match = page.Topics?.FirstOrDefault(t => t.TopicArn.EndsWith(suffix, StringComparison.Ordinal))?.TopicArn;

            if (match is not null)
            {
                return match;
            }

            nextToken = page.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return null;
    }
}
