using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FlociLab.Core;
using Microsoft.Azure.Cosmos;

namespace FlociLab.Azure.CosmosDb;

/// <summary>
/// Azure Cosmos DB (NoSQL API) against floci-az. Ordinary Microsoft.Azure.Cosmos code — the only
/// emulator-aware line in the sample is in <see cref="CosmosDbClientFactory"/>.
///
/// <para>
/// Cosmos has a two-level hierarchy — database, then container — where every other document DB in
/// this repo is flat. <see cref="DatabaseId"/> is a fixed, persistent database created idempotently
/// on every run (<c>CreateDatabaseIfNotExistsAsync</c>) rather than per-run state: the container is
/// the resource this demo actually creates and tears down, matching the "table" DynamoDB and
/// "container" Blob work with elsewhere in the repo.
/// </para>
/// </summary>
public sealed class CosmosDbDemo(CosmosDbClientFactory factory) : IServiceDemo
{
    private const string DatabaseId = "flocilab";
    private const string Greeting = "Hello from FlociLab.";

    public string Provider => CloudProvider.Azure;

    public string Slug => "cosmosdb";

    public string DisplayName => "Cosmos DB (NoSQL)";

    public string Category => "Database";

    public string Route => "/azure/cosmosdb";

    /// <summary>
    /// ReadAccount — the cheapest call Cosmos has: one request, no database or container required
    /// to exist. The direct analog of ListTables/ListContainers elsewhere in the repo, which all
    /// enumerate at the account level rather than inside a specific resource.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            CosmosClient client = factory.Create();

            // ReadAccountAsync is the one call in this sample with no CancellationToken overload,
            // so the token is applied from outside instead. That is load-bearing, not tidiness:
            // CoverageMatrix enforces ProbeTimeout *only* by cancelling the token it passes in, so
            // without this the Cosmos cell could never report "No response within 5s" and
            // /coverage would block on the SDK's own budget — RequestTimeout plus retries in
            // emulator mode, and in real-cloud mode no RequestTimeout is set at all.
            AccountProperties account = await client.ReadAccountAsync().WaitAsync(ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ReadAccount returned '{account.Id}'.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Building the client can itself fail — UseEmulator=false with no CosmosConnectionString
        // throws before any request goes out, as does a malformed endpoint or key. That has to
        // become a failed step like any other: an iterator that throws on the first MoveNextAsync
        // takes down the circuit instead of rendering the reason. Caught here and yielded below,
        // because C# forbids a yield inside a try that has a catch. Same shape as QueueDemo.
        //
        // Cached client — see CosmosDbClientFactory's remarks. No `using` here, unlike the AWS
        // sample: disposing a cached client would break every run after the first.
        CosmosClient? client = null;
        Exception? clientFailure = null;

        try
        {
            client = factory.Create();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clientFailure = ex;
        }

        if (client is null)
        {
            yield return DemoStep.Failed("CosmosClient", clientFailure!, "new CosmosClient(accountEndpoint, key)");

            yield break;
        }

        // Unique per run, so two runs never collide and a leftover container from a crashed run
        // never makes the next one fail. Cosmos container ids allow up to 255 characters.
        string containerId = $"flocilab-cosmosdb-{Guid.NewGuid():N}";
        string itemId = Guid.NewGuid().ToString("N");
        bool created = false;
        bool createConfirmed = false;

        Database database;

        // Assigned unconditionally right after the EnsureDatabase step below, before anything that
        // could set `created = true` — so by the time `finally` reads it via the null-forgiving
        // operator, it is always non-null. Nullable here only to satisfy definite-assignment
        // analysis across the intervening yield.
        Container? container = null;
        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "EnsureDatabase",
                $"POST {factory.ServiceUrl}/dbs\ncosmosClient.CreateDatabaseIfNotExistsAsync(\"{DatabaseId}\")",
                async () =>
                {
                    DatabaseResponse response = await client.CreateDatabaseIfNotExistsAsync(DatabaseId, cancellationToken: ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.StatusCode} — database '{DatabaseId}' "
                        + (response.StatusCode == HttpStatusCode.Created ? "created" : "already existed");
                }).ConfigureAwait(false);

            database = client.GetDatabase(DatabaseId);
            container = database.GetContainer(containerId);

            yield return await RunStepAsync(
                "ListContainers — before",
                $"GET {factory.ServiceUrl}/dbs/{DatabaseId}/colls\ndatabase.GetContainerQueryIterator<ContainerProperties>()",
                async () =>
                {
                    List<string> names = [];

                    using FeedIterator<ContainerProperties> iterator = database.GetContainerQueryIterator<ContainerProperties>();

                    while (iterator.HasMoreResults)
                    {
                        FeedResponse<ContainerProperties> page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                        names.AddRange(page.Select(c => $"  {c.Id}"));
                    }

                    return $"{names.Count} container(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateContainer",
                $"POST {factory.ServiceUrl}/dbs/{DatabaseId}/colls\ndatabase.CreateContainerAsync(\"{containerId}\", partitionKeyPath: \"/id\")",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the container exists and cleanup has to know about it.
                    // Cleanup treats an absent container as a no-op, so claiming it early is free.
                    created = true;
                    ContainerResponse response = await database.CreateContainerAsync(containerId, "/id", cancellationToken: ct).ConfigureAwait(false);

                    // Distinct from `created`: this one means the create is known to have landed,
                    // which is what lets cleanup tell "already gone" from "never existed".
                    createConfirmed = true;

                    return $"HTTP {(int)response.StatusCode} — ETag: {response.Resource.ETag}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpsertItem",
                $"POST {factory.ServiceUrl}/dbs/{DatabaseId}/colls/{containerId}/docs\nx-ms-documentdb-partitionkey: [\"{itemId}\"]\ncontainer.UpsertItemStreamAsync({{ id: \"{itemId}\", greeting: \"{Greeting}\" }})",
                async () =>
                {
                    await using MemoryStream body = JsonBody(itemId, Greeting);

                    using ResponseMessage response = await container.UpsertItemStreamAsync(body, new PartitionKey(itemId), cancellationToken: ct)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    return $"HTTP {(int)response.StatusCode} — ETag: {response.Headers.ETag}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ReadItem",
                $"GET {factory.ServiceUrl}/dbs/{DatabaseId}/colls/{containerId}/docs/{itemId}\nx-ms-documentdb-partitionkey: [\"{itemId}\"]\ncontainer.ReadItemStreamAsync(\"{itemId}\", new PartitionKey(\"{itemId}\"))",
                async () =>
                {
                    using ResponseMessage response = await container.ReadItemStreamAsync(itemId, new PartitionKey(itemId), cancellationToken: ct).ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    using StreamReader reader = new(response.Content);
                    string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                    // A round-trip that found nothing did not round-trip. The lede promises this
                    // page shows what floci-az actually answered, so an empty get goes out red —
                    // steps that all show green for a run that never read its own write would be
                    // the page lying.
                    if (!body.Contains(Greeting, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.StatusCode} — the item put above did not come back:\n{body}");
                    }

                    return $"HTTP {(int)response.StatusCode}\n\n{body}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteItem",
                $"DELETE {factory.ServiceUrl}/dbs/{DatabaseId}/colls/{containerId}/docs/{itemId}\nx-ms-documentdb-partitionkey: [\"{itemId}\"]\ncontainer.DeleteItemStreamAsync(\"{itemId}\", new PartitionKey(\"{itemId}\"))",
                async () =>
                {
                    using ResponseMessage response = await container.DeleteItemStreamAsync(itemId, new PartitionKey(itemId), cancellationToken: ct).ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    return $"HTTP {(int)response.StatusCode}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean database. The database itself is left in
            // place — it is shared, persistent scope, not this run's resource. The step it
            // produces is yielded below — an iterator may not yield from inside a finally.
            cleanup = created
                ? await DeleteContainerAsync(container!, containerId, factory.ServiceUrl, createConfirmed, ct).ConfigureAwait(false)
                : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// The Cosmos SDK reports every service-level failure as a <see cref="CosmosException"/>, but
    /// unlike <c>RequestFailedException</c> or <c>AmazonServiceException</c> it has no
    /// "status 0" sentinel marking a wrapped transport failure — so this walk cannot use a single
    /// blanket case for "any CosmosException is Error" the way the Blob and DynamoDB samples do;
    /// doing that would return Error before ever inspecting the SocketException a transport failure
    /// might carry as an inner exception. Instead the loop only claims the specific statuses it
    /// recognises, and falls through afterwards.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case CosmosException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);
            }
        }

        // Nothing transport-shaped anywhere in the chain, so a CosmosException here is the
        // emulator answering with a status this sample does not special-case.
        return ex is CosmosException
            ? ProbeResult.Error(Describe(ex), elapsed)
            : ProbeResult.FromException(ex, elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Cosmos DB would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the
        // container. Catching it here would instead fabricate a "Failed" step for every remaining
        // operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// Cleanup. The call uses <see cref="CancellationToken.None"/> — a run that was cancelled
    /// still has a container to remove.
    /// </summary>
    private static async Task<DemoStep> DeleteContainerAsync(Container container, string containerId, string serviceUrl, bool createConfirmed, CancellationToken ct)
    {
        string request = $"DELETE {serviceUrl}/dbs/{DatabaseId}/colls/{containerId}\ncontainer.DeleteContainerAsync()";

        return await RunStepAsync("DeleteContainer — cleanup", request, async () =>
        {
            try
            {
                ContainerResponse response = await container.DeleteContainerAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.StatusCode} — removed the container"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            // A 404 has two very different causes and only one of them is benign, so it goes out
            // red either way rather than as a green step asserting the wrong one. If the create
            // above is known to have landed, the container was removed by something other than
            // this run — that is a leaked-then-vanished resource, not a clean no-op.
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"HTTP 404 — nothing was removed: "
                    + (createConfirmed
                        ? $"'{containerId}' was created by this run but is already gone, so something else deleted it."
                        : $"'{containerId}' never existed, because CreateContainer above did not succeed."),
                    ex);
            }
        }).ConfigureAwait(false);
    }

    private static MemoryStream JsonBody(string id, string greeting)
        => new(JsonSerializer.SerializeToUtf8Bytes(new { id, greeting }));

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
