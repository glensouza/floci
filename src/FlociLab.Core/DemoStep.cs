namespace FlociLab.Core;

/// <summary>
/// One operation in a demo run. <see cref="Request"/> and <see cref="Response"/> carry the raw
/// wire traffic — that is the part of the UI worth watching, so keep them populated.
/// </summary>
public sealed record DemoStep(
    string Title,
    string? Request = null,
    string? Response = null,
    bool Succeeded = true,
    string? Error = null)
{
    public static DemoStep Failed(string title, Exception ex, string? request = null)
        => new(title, request, null, false, ex.Message);
}
