namespace FlociLab.Gcp.SecretManager;

/// <summary>
/// In proto3 a singular message field is absent rather than defaulted, so its generated getter
/// returns <c>null</c> when the server omits it — and a field floci-gcp leaves out would otherwise
/// surface as a bare <see cref="NullReferenceException"/>. "Object reference not set to an instance
/// of an object" on a red step tells a viewer nothing about which operation answered short. These
/// name it. Same helper as the AWS sample's <c>SecretsManagerResponse</c> and the KMS sample's
/// <c>KmsResponse</c>; each sample keeps its own copy rather than sharing one, because a sample has
/// to stay clonable on its own.
/// </summary>
internal static class SecretManagerResponse
{
    internal static T Require<T>(T? value, string operation, string field) where T : class
        => value ?? throw new InvalidOperationException($"{operation} answered, but without {field} — a field proto3 marks optional and real Secret Manager always sends.");
}
