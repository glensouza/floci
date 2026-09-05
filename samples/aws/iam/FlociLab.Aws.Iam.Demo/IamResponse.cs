namespace FlociLab.Aws.Iam;

/// <summary>
/// AWSSDK v4 marks response members nullable, so a field the emulator omits would otherwise
/// surface as a bare <see cref="NullReferenceException"/> — and a demo step whose error reads
/// "Object reference not set to an instance of an object" tells the viewer nothing about which
/// field floci left out. These name it.
/// </summary>
internal static class IamResponse
{
    internal static T Require<T>(T? value, string operation, string field) where T : class
        => value ?? throw new InvalidOperationException($"{operation} answered, but without {field} — a field the SDK marks optional and real IAM always sends.");
}
