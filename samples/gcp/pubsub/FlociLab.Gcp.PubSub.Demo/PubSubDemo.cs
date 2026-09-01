using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using FlociLab.Core;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;

namespace FlociLab.Gcp.PubSub;

/// <summary>
/// Google Cloud Pub/Sub against floci-gcp. Ordinary Google.Cloud.PubSub.V1 code — the only
/// emulator-aware lines in the sample are in <see cref="PubSubClientFactory"/>.
///
/// <para>
/// Unlike SQS or Azure's queues, Pub/Sub has no single "queue" resource: a topic accepts
/// publishes and a subscription is what a reader pulls from, and a subscription only ever sees
/// messages published after it existed. The round-trip below creates both, in that order, before
/// publishing — publishing first would leave the message undelivered and the demo would fail
/// honestly rather than show a false green run.
/// </para>
/// </summary>
public sealed class PubSubDemo(PubSubClientFactory factory) : IServiceDemo
{
    private const string MessageBody = "Hello from FlociLab.";

    public string Provider => CloudProvider.Gcp;

    public string Slug => "pubsub";

    public string DisplayName => "Pub/Sub";

    public string Category => "Messaging";

    public string Route => "/gcp/pubsub";

    /// <summary>ListTopics — one request, no state, and the cheapest call Pub/Sub has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            PublisherServiceApiClient publisher = factory.Publisher();
            int count = 0;

            await foreach (Topic topic in publisher.ListTopicsAsync(ProjectName.FromProject(factory.ProjectId))
                .WithCancellation(ct).ConfigureAwait(false))
            {
                _ = topic;
                count++;
            }

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListTopics returned {count} topic(s).");
        }
        catch (RpcException ex) when (IsCancellation(ex, ct))
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        PublisherServiceApiClient publisher = factory.Publisher();
        SubscriberServiceApiClient subscriber = factory.Subscriber();

        // Unique per run, so two runs never collide and a leftover topic/subscription from a
        // crashed run never makes the next one fail. Pub/Sub allows up to 255 chars of
        // letters/digits/hyphens/underscores/periods/tildes/plus/percent.
        string resourceName = $"flocilab-pubsub-{Guid.NewGuid():N}";
        TopicName topicName = TopicName.FromProjectTopic(factory.ProjectId, resourceName);
        SubscriptionName subscriptionName = SubscriptionName.FromProjectSubscription(factory.ProjectId, resourceName);
        bool topicCreated = false;
        bool subscriptionCreated = false;
        string? ackId = null;

        DemoStep? deleteSubscriptionStep;
        DemoStep? deleteTopicStep;

        try
        {
            yield return await RunStepAsync(
                "ListTopics — before",
                $"{factory.GrpcTarget} google.pubsub.v1.Publisher/ListTopics\npublisher.ListTopicsAsync(\"{factory.ProjectId}\")",
                ct,
                async () =>
                {
                    List<string> names = [];

                    await foreach (Topic topic in publisher.ListTopicsAsync(ProjectName.FromProject(factory.ProjectId))
                        .WithCancellation(ct).ConfigureAwait(false))
                    {
                        names.Add($"  {topic.Name}");
                    }

                    return $"{names.Count} topic(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateTopic",
                $"{factory.GrpcTarget} google.pubsub.v1.Publisher/CreateTopic\npublisher.CreateTopicAsync(\"{topicName}\")",
                ct,
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the topic exists and cleanup has to know about it. Cleanup
                    // treats an absent topic as a no-op, so claiming it early is free.
                    topicCreated = true;
                    Topic response = await publisher.CreateTopicAsync(topicName, ct)
                        .ConfigureAwait(false);

                    return $"Topic {response.Name}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateSubscription",
                $"{factory.GrpcTarget} google.pubsub.v1.Subscriber/CreateSubscription\n"
                    + $"subscriber.CreateSubscriptionAsync(\"{subscriptionName}\", \"{topicName}\", pushConfig: null, ackDeadlineSeconds: 10)",
                ct,
                async () =>
                {
                    // Same reasoning as topicCreated: claim it before the call lands.
                    subscriptionCreated = true;
                    Subscription response = await subscriber.CreateSubscriptionAsync(
                        subscriptionName, topicName, pushConfig: null, ackDeadlineSeconds: 10, ct)
                        .ConfigureAwait(false);

                    return $"Subscription {response.Name}\n  topic: {response.Topic}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Publish",
                $"{factory.GrpcTarget} google.pubsub.v1.Publisher/Publish\n"
                    + $"publisher.PublishAsync(\"{topicName}\", [new PubsubMessage {{ Data = \"{MessageBody}\" }}])",
                ct,
                async () =>
                {
                    PublishResponse response = await publisher.PublishAsync(
                        topicName, [new PubsubMessage { Data = ByteString.CopyFromUtf8(MessageBody) }], ct)
                        .ConfigureAwait(false);

                    return $"MessageIds: {string.Join(", ", response.MessageIds)}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Pull",
                $"{factory.GrpcTarget} google.pubsub.v1.Subscriber/Pull\n"
                    + $"subscriber.PullAsync(\"{subscriptionName}\", maxMessages: 1)",
                ct,
                async () =>
                {
                    PullResponse response = await subscriber.PullAsync(subscriptionName, maxMessages: 1, ct)
                        .ConfigureAwait(false);
                    ReceivedMessage? received = response.ReceivedMessages.FirstOrDefault();
                    ackId = received?.AckId;

                    // A pull that received nothing did not round-trip. The lede promises this page
                    // shows what floci-gcp actually answered, so an empty pull goes out red — five
                    // green steps for a run that delivered no message is the page lying.
                    if (received is null)
                    {
                        throw new InvalidOperationException(
                            "0 message(s); the message published above did not arrive.");
                    }

                    return $"AckId: {received.AckId}\nBody: {received.Message.Data.ToStringUtf8()}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Acknowledge",
                $"{factory.GrpcTarget} google.pubsub.v1.Subscriber/Acknowledge\nsubscriber.AcknowledgeAsync(\"{subscriptionName}\", [\"{ackId}\"])",
                ct,
                async () =>
                {
                    if (ackId is null)
                    {
                        throw new InvalidOperationException("Skipped — Pull returned no message to acknowledge.");
                    }

                    await subscriber.AcknowledgeAsync(subscriptionName, [ackId], ct).ConfigureAwait(false);

                    return "Acknowledged.";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean project. The subscription goes before the
            // topic it depends on — the steps are yielded below, an iterator may not yield from
            // inside a finally.
            deleteSubscriptionStep = subscriptionCreated
                ? await DeleteSubscriptionAsync(subscriber, subscriptionName).ConfigureAwait(false)
                : null;
            deleteTopicStep = topicCreated
                ? await DeleteTopicAsync(publisher, topicName).ConfigureAwait(false)
                : null;
        }

        if (deleteSubscriptionStep is not null)
        {
            yield return deleteSubscriptionStep;
        }

        if (deleteTopicStep is not null)
        {
            yield return deleteTopicStep;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> cannot see a gRPC status hiding inside an
    /// <see cref="RpcException"/>, which is where this SDK puts every answer the server gave. A
    /// refused connection surfaces as <see cref="StatusCode.Unavailable"/> too, so the transport
    /// case has to be told apart from the emulator genuinely answering "unavailable" — which
    /// floci-gcp does not do, so treating every Unavailable as unreachable is the honest read here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RpcException { StatusCode: StatusCode.Unimplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                // DeadlineExceeded is GAX's own per-call expiration rather than this token: the
                // emulator accepted the connection and never answered, which is the same story
                // Unavailable tells and must not read as the sample being broken.
                case RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded }:
                case SocketException or TimeoutException:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // Any other status means the emulator answered, so this is it behaving badly
                // rather than being absent. Stop unwrapping and report the error.
                case RpcException:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Whether an <see cref="RpcException"/> is this token being cancelled rather than the server
    /// answering. Only a token already cancelled when the call starts throws
    /// <see cref="OperationCanceledException"/>; one that trips mid-flight surfaces as
    /// <see cref="StatusCode.Cancelled"/> instead, because the SDK reports it the way the wire
    /// carried it. Everything upstream keys off <see cref="OperationCanceledException"/> —
    /// <c>CoverageMatrix</c> enforces <c>FlociOptions.ProbeTimeout</c> by cancelling a linked token
    /// and rendering that exception as "No response within 5s", and <see cref="RunStepAsync"/>
    /// treats it as the run stopping rather than a step failing — so without this translation a
    /// wedged emulator reads as <c>Error</c> and a user navigating away mid-run paints the
    /// remaining steps red. Gated on the token actually being cancelled: a <c>Cancelled</c> status
    /// nobody asked for is the server misbehaving, which is a genuine error.
    /// </summary>
    private static bool IsCancellation(RpcException ex, CancellationToken ct)
        => ct.IsCancellationRequested && ex.StatusCode == StatusCode.Cancelled;

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Pub/Sub would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, CancellationToken ct, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        catch (RpcException ex) when (IsCancellation(ex, ct))
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the topic and
        // subscription. Catching it here would instead fabricate a "Failed" step for every
        // remaining operation, reporting the user navigating away as the emulator misbehaving.
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
    /// Cleanup. Uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// subscription to remove.
    /// </summary>
    private static async Task<DemoStep> DeleteSubscriptionAsync(SubscriberServiceApiClient subscriber, SubscriptionName subscriptionName)
    {
        string request = $"google.pubsub.v1.Subscriber/DeleteSubscription\nsubscriber.DeleteSubscriptionAsync(\"{subscriptionName}\")";

        return await RunStepAsync("DeleteSubscription — cleanup", request, CancellationToken.None, async () =>
        {
            try
            {
                await subscriber.DeleteSubscriptionAsync(subscriptionName).ConfigureAwait(false);
            }
            // CreateSubscription claims the name before it calls, so the subscription may never
            // have been made — that is a clean run finishing, not a cleanup failure worth showing
            // in red.
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return "Nothing to remove — the subscription was never created.";
            }

            return "Removed the subscription.";
        }).ConfigureAwait(false);
    }

    /// <summary>Cleanup. Same reasoning as <see cref="DeleteSubscriptionAsync"/>.</summary>
    private static async Task<DemoStep> DeleteTopicAsync(PublisherServiceApiClient publisher, TopicName topicName)
    {
        string request = $"google.pubsub.v1.Publisher/DeleteTopic\npublisher.DeleteTopicAsync(\"{topicName}\")";

        return await RunStepAsync("DeleteTopic — cleanup", request, CancellationToken.None, async () =>
        {
            try
            {
                await publisher.DeleteTopicAsync(topicName).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return "Nothing to remove — the topic was never created.";
            }

            return "Removed the topic.";
        }).ConfigureAwait(false);
    }
}
