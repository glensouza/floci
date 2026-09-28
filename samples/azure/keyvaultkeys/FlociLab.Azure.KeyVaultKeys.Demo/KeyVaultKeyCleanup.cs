using Azure.Security.KeyVault.Keys;

namespace FlociLab.Azure.KeyVaultKeys;

/// <summary>
/// The one delete path both <see cref="KeyVaultKeysDemo"/> and <see cref="KeyVaultKeyManagement"/>
/// use: a soft delete followed by a purge, by name, on <see cref="CancellationToken.None"/> — a run
/// that was cancelled still has a key to remove.
/// </summary>
internal static class KeyVaultKeyCleanup
{
    /// <summary>
    /// Returns the delete's failure when its reply could not be read but the purge still ran, or
    /// <c>null</c> when both went cleanly. A status the vault answered with — a 404 for a key that
    /// never existed included — propagates, as does any failure of the purge itself.
    /// </summary>
    internal static async Task<Exception?> DeleteAndPurgeAsync(KeyClient client, string name)
    {
        Exception? unreadableReply = null;

        try
        {
            DeleteKeyOperation operation = await client.StartDeleteKeyAsync(name, CancellationToken.None).ConfigureAwait(false);
            await operation.WaitForCompletionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        // Only a reply the SDK could not parse, never an answered status. floci-az 0.13.0 soft-deletes
        // the key and then sends attributes.nbf/exp as JSON null (docs/BLAZOR-PLAN.md §14), so the
        // delete has landed and the purge below still has a key to remove — verified 2026-09-28: a
        // GetKey after it answers 404, and the purge answers 200. Returned rather than swallowed, so
        // every caller still reports it.
        catch (InvalidOperationException ex)
        {
            unreadableReply = ex;
        }

        await client.PurgeDeletedKeyAsync(name, CancellationToken.None).ConfigureAwait(false);

        return unreadableReply;
    }
}
