using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// Keeps <see cref="AzureKeyVaultSecretsTests"/> and <see cref="AzureKeyVaultKeysTests"/> from
/// running at the same time. They are the only two classes in the suite whose sample authenticates
/// with a <c>TokenCredential</c>, and <c>FlociAzureExtensions.Credential</c> points
/// <c>ManagedIdentityCredential</c> at an emulator through <c>AZURE_POD_IDENTITY_AUTHORITY_HOST</c>
/// — one process-wide variable, so two classes each targeting their own throwaway container cannot
/// both be right.
///
/// <para>
/// xunit.v3 runs collections in parallel by default and this project sets no
/// <c>xunit.runner.json</c>, so without this the two classes interleave: one class's
/// <c>Probe_Reports_Unreachable_When_Nothing_Is_Listening</c> aims the authority host at the dead
/// <c>127.0.0.1:1</c>, and a token acquisition in the other class during that window fails against
/// IMDS — reporting <c>Unreachable</c>, or an <c>AuthenticationFailedException</c> where a
/// <c>RequestFailedException</c> is asserted, for a container that is up. Tests within one
/// collection run sequentially, which is exactly the guarantee <c>Credential</c>'s
/// "last value this method wrote" tracking assumes.
/// </para>
/// </summary>
[CollectionDefinition(nameof(AzureKeyVaultCollection), DisableParallelization = true)]
public sealed class AzureKeyVaultCollection;
