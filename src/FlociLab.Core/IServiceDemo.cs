namespace FlociLab.Core;

/// <summary>
/// Every service sample implements this — one implementation per emulated service.
/// </summary>
public interface IServiceDemo
{
    /// <summary>"aws" | "azure" | "gcp" | "oci". Use the <see cref="CloudProvider"/> constants.</summary>
    string Provider { get; }

    /// <summary>Stable slug used in routes: "s3", "servicebus", "pubsub".</summary>
    string Slug { get; }

    string DisplayName { get; }

    /// <summary>"Storage" | "Messaging" | "Compute" | "Security" | ...</summary>
    string Category { get; }

    /// <summary>Route into the owning RCL page, e.g. "/azure/servicebus".</summary>
    string Route { get; }

    /// <summary>
    /// Cheapest possible list/describe call. Drives the coverage matrix, so it MUST
    /// distinguish <see cref="ProbeStatus.NotImplemented"/> (501) from
    /// <see cref="ProbeStatus.Unreachable"/> from <see cref="ProbeStatus.Ok"/>.
    /// </summary>
    Task<ProbeResult> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// Scripted create -> read -> delete round-trip, one <see cref="DemoStep"/> per operation.
    /// Implementations clean up in a <c>finally</c> so re-runs are idempotent, use a unique
    /// per-run resource name, and never swallow an exception — a failure is yielded as a
    /// step with <see cref="DemoStep.Succeeded"/> false.
    /// </summary>
    IAsyncEnumerable<DemoStep> RunAsync(CancellationToken ct);
}

/// <summary>The four provider slugs, so nothing has to spell them as literals.</summary>
public static class CloudProvider
{
    public const string Aws = "aws";
    public const string Azure = "azure";
    public const string Gcp = "gcp";
    public const string Oci = "oci";

    /// <summary>Provider slugs in the order they appear in the UI.</summary>
    public static readonly IReadOnlyList<string> All = [Aws, Azure, Gcp, Oci];

    /// <summary>Position in <see cref="All"/>, for stable UI ordering. Unknown slugs sort last.</summary>
    public static int Order(string provider)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == provider)
            {
                return i;
            }
        }

        return All.Count;
    }

    public static string DisplayName(string provider) => provider switch
    {
        Aws => "AWS",
        Azure => "Azure",
        Gcp => "GCP",
        Oci => "OCI",
        _ => provider,
    };
}
