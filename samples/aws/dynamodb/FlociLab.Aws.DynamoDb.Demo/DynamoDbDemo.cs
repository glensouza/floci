using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.DynamoDb;

/// <summary>
/// Amazon DynamoDB against floci. Ordinary AWSSDK.DynamoDBv2 code — the only emulator-aware line
/// in the sample is in <see cref="DynamoDbClientFactory"/>.
/// </summary>
public sealed class DynamoDbDemo(DynamoDbClientFactory factory) : IServiceDemo
{
    private const string Greeting = "Hello from FlociLab.";

    public string Provider => CloudProvider.Aws;

    public string Slug => "dynamodb";

    public string DisplayName => "DynamoDB";

    public string Category => "Database";

    public string Route => "/aws/dynamodb";

    /// <summary>ListTables — one request, no state, and the cheapest call DynamoDB has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonDynamoDB client = factory.Create();
            ListTablesResponse response = await client.ListTablesAsync(new ListTablesRequest(), ct).ConfigureAwait(false);
            int count = response.TableNames?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListTables returned {count} table(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonDynamoDB client = factory.Create();

        // Unique per run, so two runs never collide and a leftover table from a crashed run never
        // makes the next one fail. DynamoDB allows up to 255 chars of alphanumerics/./-/_.
        string tableName = $"flocilab-dynamodb-{Guid.NewGuid():N}";
        string itemId = Guid.NewGuid().ToString("N");
        bool created = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListTables — before",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: DynamoDB_20120810.ListTables\nclient.ListTablesAsync(new ListTablesRequest())",
                async () =>
                {
                    ListTablesResponse response = await client.ListTablesAsync(new ListTablesRequest(), ct).ConfigureAwait(false);
                    IEnumerable<string> names = response.TableNames?.Select(n => $"  {n}") ?? [];

                    return $"HTTP {(int)response.HttpStatusCode} — {response.TableNames?.Count ?? 0} table(s)\n"
                        + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateTable",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: DynamoDB_20120810.CreateTable\nclient.CreateTableAsync(new CreateTableRequest {{ TableName = \"{tableName}\", KeySchema = [{{ id, HASH }}], AttributeDefinitions = [{{ id, S }}], BillingMode = PAY_PER_REQUEST }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the table exists and cleanup has to know about it. Cleanup
                    // treats an absent table as a no-op, so claiming it early is free.
                    created = true;
                    CreateTableResponse response = await client.CreateTableAsync(
                        new CreateTableRequest
                        {
                            TableName = tableName,
                            BillingMode = BillingMode.PAY_PER_REQUEST,
                            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
                            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
                        }, ct).ConfigureAwait(false);

                    // floci returns ACTIVE synchronously, so this loop never iterates against the
                    // emulator. Real AWS answers CREATING and needs seconds to reach ACTIVE, and
                    // PutItem below would race a table that cannot take writes yet. The bound is
                    // 30 polls — 30 s of delay plus the DescribeTable round-trips — so a stuck
                    // table fails here loudly instead of hanging "Running…" forever.
                    TableStatus status = response.TableDescription.TableStatus;

                    for (int attempt = 0; status == TableStatus.CREATING && attempt < 30; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                        DescribeTableResponse describe = await client.DescribeTableAsync(
                            new DescribeTableRequest { TableName = tableName }, ct).ConfigureAwait(false);
                        status = describe.Table.TableStatus;
                    }

                    // Same rule as GetItem below: a step that did not achieve what it claims does
                    // not get a green badge. Giving up on a still-CREATING table and returning a
                    // success string would show green here and then an unexplained
                    // ResourceNotFoundException on PutItem, blaming the write for the create.
                    if (status != TableStatus.ACTIVE)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — the table was still {status} after 30 polls; it never became ACTIVE.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — TableStatus: {status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutItem",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: DynamoDB_20120810.PutItem\nclient.PutItemAsync(new PutItemRequest {{ TableName = \"{tableName}\", Item = {{ id = \"{itemId}\", greeting = \"{Greeting}\" }} }})",
                async () =>
                {
                    PutItemResponse response = await client.PutItemAsync(
                        new PutItemRequest
                        {
                            TableName = tableName,
                            Item = new Dictionary<string, AttributeValue>
                            {
                                ["id"] = new AttributeValue(itemId),
                                ["greeting"] = new AttributeValue(Greeting),
                            },
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetItem",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: DynamoDB_20120810.GetItem\nclient.GetItemAsync(new GetItemRequest {{ TableName = \"{tableName}\", Key = {{ id = \"{itemId}\" }} }})",
                async () =>
                {
                    GetItemResponse response = await client.GetItemAsync(
                        new GetItemRequest
                        {
                            TableName = tableName,
                            Key = new Dictionary<string, AttributeValue> { ["id"] = new AttributeValue(itemId) },
                        }, ct).ConfigureAwait(false);

                    // A round-trip that found nothing did not round-trip. The lede promises this
                    // page shows what floci actually answered, so an empty get goes out red — five
                    // green steps for a run that never read its own write is the page lying.
                    if (!response.IsItemSet)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — no item; the item put above did not come back.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — greeting: {response.Item["greeting"].S}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteItem",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: DynamoDB_20120810.DeleteItem\nclient.DeleteItemAsync(new DeleteItemRequest {{ TableName = \"{tableName}\", Key = {{ id = \"{itemId}\" }} }})",
                async () =>
                {
                    DeleteItemResponse response = await client.DeleteItemAsync(
                        new DeleteItemRequest
                        {
                            TableName = tableName,
                            Key = new Dictionary<string, AttributeValue> { ["id"] = new AttributeValue(itemId) },
                        }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await this.DeleteTableAsync(client, tableName, ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// The AWS SDK reports both of the interesting failures inside an
    /// <see cref="AmazonServiceException"/>, so <see cref="ProbeResult.FromException"/> — which
    /// inspects only the outermost exception — cannot classify them on its own. A 501 arrives as
    /// a status code on the exception; a refused connection arrives with no status code at all
    /// and a transport exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real DynamoDB would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the table.
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// DeleteTable addresses the table by name directly — unlike SQS, DynamoDB has no separate
    /// name-to-URL resolution step, so cleanup is one call. A table that genuinely never got
    /// created answers with <see cref="ResourceNotFoundException"/>, which is a clean run
    /// finishing, not a cleanup failure worth showing in red. The call uses
    /// <see cref="CancellationToken.None"/> — a run that was cancelled still has a table to remove.
    /// </summary>
    private async Task<DemoStep> DeleteTableAsync(IAmazonDynamoDB client, string tableName, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nX-Amz-Target: DynamoDB_20120810.DeleteTable\nclient.DeleteTableAsync(new DeleteTableRequest {{ TableName = \"{tableName}\" }})";

        return await RunStepAsync("DeleteTable — cleanup", request, async () =>
        {
            try
            {
                DeleteTableResponse response = await client.DeleteTableAsync(
                    new DeleteTableRequest { TableName = tableName }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — removed the table"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (ResourceNotFoundException)
            {
                return "Nothing to remove — the table was never created.";
            }
        }).ConfigureAwait(false);
    }
}
