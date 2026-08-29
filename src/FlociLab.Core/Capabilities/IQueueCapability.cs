namespace FlociLab.Core.Capabilities;

/// <summary>SQS · Azure Queue Storage + Service Bus · Pub/Sub · OCI Queue.</summary>
public interface IQueueCapability : ICloudCapability
{
    Task<IReadOnlyList<QueueInfo>> ListQueuesAsync(CancellationToken ct);

    Task CreateQueueAsync(string name, CancellationToken ct);

    Task SendMessageAsync(string queue, string body, CancellationToken ct);

    /// <summary>Receives up to <paramref name="maxMessages"/> and deletes/acks what it returns.</summary>
    Task<IReadOnlyList<QueueMessage>> ReceiveMessagesAsync(string queue, int maxMessages, CancellationToken ct);

    Task DeleteQueueAsync(string name, CancellationToken ct);
}

public sealed record QueueInfo(string Name, int? ApproximateMessageCount = null);

public sealed record QueueMessage(string Id, string Body);
