namespace FlociLab.Core.Capabilities;

/// <summary>One message received from a queue.</summary>
public sealed record QueueMessage(string Id, string Body);
