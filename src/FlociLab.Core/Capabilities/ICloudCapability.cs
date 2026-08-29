namespace FlociLab.Core.Capabilities;

/// <summary>
/// Base of the five capability interfaces. A sample implements one of them only where a genuine
/// cross-cloud analog exists (docs/BLAZOR-PLAN.md §6) — services with no analog simply don't
/// appear in the comparison pages. the FlociLab.Comparison RCL consumes these and never
/// references a provider SDK.
/// </summary>
public interface ICloudCapability
{
    /// <summary>"aws" | "azure" | "gcp" | "oci" — the comparison column this fills.</summary>
    string Provider { get; }

    /// <summary>The real service behind the column, e.g. "Amazon S3", "Azure Blob Storage".</summary>
    string ServiceName { get; }
}
