namespace FlociLab.Aws.SecretsManager;

/// <summary>
/// AWSSDK v4 marks response members nullable, so a field the emulator omits would otherwise
/// surface as a bare <see cref="NullReferenceException"/> — and an error that reads "Object
/// reference not set to an instance of an object" tells the viewer nothing about which field
/// floci left out. These name it. Same helper as the KMS sample's <c>KmsResponse</c>; each
/// sample keeps its own copy rather than sharing one, because a sample has to stay clonable on
/// its own.
/// </summary>
internal static class SecretsManagerResponse
{
    internal static T Require<T>(T? value, string operation, string field) where T : class
        => value ?? throw new InvalidOperationException($"{operation} answered, but without {field} — a field the SDK marks optional and real Secrets Manager always sends.");
}
