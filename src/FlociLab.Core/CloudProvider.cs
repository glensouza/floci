namespace FlociLab.Core;

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
        for (int i = 0; i < All.Count; i++)
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
