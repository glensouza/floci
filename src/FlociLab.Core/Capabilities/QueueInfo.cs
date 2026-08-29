namespace FlociLab.Core.Capabilities;

/// <summary>A queue, in whatever the provider calls it.</summary>
public sealed record QueueInfo(string Name, int? ApproximateMessageCount = null);
