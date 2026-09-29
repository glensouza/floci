using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Route53;

/// <summary>
/// Route 53 hosted zones and record sets against floci. Ordinary AWSSDK.Route53 code — the only
/// emulator-aware line in the sample is in <see cref="Route53ClientFactory"/>.
/// </summary>
public sealed class Route53Demo(Route53ClientFactory factory) : IServiceDemo
{
    // 192.0.2.0/24 is TEST-NET-1 (RFC 5737), and .test is reserved (RFC 2606): nothing here can
    // ever resolve to, or be delegated from, a real host — which matters when the page is pointed
    // at a real account.
    private const string RecordAddress = "192.0.2.10";
    private const long RecordTtl = 60;

    public string Provider => CloudProvider.Aws;

    public string Slug => "route53";

    public string DisplayName => "Route 53";

    public string Category => "Networking";

    public string Route => "/aws/route53";

    /// <summary>ListHostedZones capped at one — one request, no state, and the cheapest call Route 53 has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonRoute53 client = factory.Create();
            ListHostedZonesResponse response = await client.ListHostedZonesAsync(new ListHostedZonesRequest { MaxItems = "1" }, ct).ConfigureAwait(false);
            int count = response.HostedZones?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListHostedZones returned {count} zone(s) on the first page.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonRoute53 client = factory.Create();

        // Unique per run, so two runs never collide and a zone left by a crashed run never makes
        // the next one fail. The trailing dot is the fully-qualified form Route 53 stores.
        string run = Guid.NewGuid().ToString("N");
        string zoneName = $"{run}.flocilab.test.";
        string recordName = $"www.{zoneName}";
        string callerReference = run;

        string? zoneId = null;
        string? changeId = null;
        bool recordPresent = false;
        List<string> strayZoneIds = [];

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "ListHostedZones — before",
                $"GET {factory.ServiceUrl}/2013-04-01/hostedzone\nclient.ListHostedZonesAsync(new ListHostedZonesRequest())",
                async () =>
                {
                    ListHostedZonesResponse response = await client.ListHostedZonesAsync(new ListHostedZonesRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.HostedZones?.Count ?? 0} zone(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateHostedZone",
                $"POST {factory.ServiceUrl}/2013-04-01/hostedzone\nclient.CreateHostedZoneAsync(new CreateHostedZoneRequest {{ Name = \"{zoneName}\", CallerReference = \"{callerReference}\" }})",
                async () =>
                {
                    CreateHostedZoneResponse response = await client.CreateHostedZoneAsync(
                        new CreateHostedZoneRequest { Name = zoneName, CallerReference = callerReference }, ct).ConfigureAwait(false);

                    // Claimed as soon as the response names it: cleanup keys off this id, and an
                    // id that never arrived means there is no zone this run can find to remove.
                    zoneId = response.HostedZone.Id;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.HostedZone.Id}, {response.DelegationSet?.NameServers?.Count ?? 0} name server(s), change {response.ChangeInfo.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateHostedZone — repeated CallerReference",
                $"POST {factory.ServiceUrl}/2013-04-01/hostedzone\nclient.CreateHostedZoneAsync(new CreateHostedZoneRequest {{ Name = \"{zoneName}\", CallerReference = \"{callerReference}\" }})",
                async () =>
                {
                    // CallerReference is Route 53's idempotency token. Replaying it must be
                    // refused rather than minting a second zone, so the refusal is the passing
                    // outcome here and a second zone is the failure.
                    try
                    {
                        CreateHostedZoneResponse response = await client.CreateHostedZoneAsync(
                            new CreateHostedZoneRequest { Name = zoneName, CallerReference = callerReference }, ct).ConfigureAwait(false);

                        strayZoneIds.Add(response.HostedZone.Id);

                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — a second zone ({response.HostedZone.Id}) was created for a repeated CallerReference.");
                    }
                    catch (HostedZoneAlreadyExistsException ex)
                    {
                        return $"HTTP {(int)ex.StatusCode} — {ex.ErrorCode}: {ex.Message}\n(refused, as real Route 53 does)";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ChangeResourceRecordSets — UPSERT",
                $"POST {factory.ServiceUrl}/2013-04-01/hostedzone/{BareId(zoneId)}/rrset\nclient.ChangeResourceRecordSetsAsync(… Action = UPSERT, {recordName} A {RecordTtl} {RecordAddress})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the record exists and the zone cannot be deleted until it goes.
                    recordPresent = true;
                    ChangeResourceRecordSetsResponse response = await client.ChangeResourceRecordSetsAsync(
                        ChangeRequest(zoneId!, ChangeAction.UPSERT, recordName), ct).ConfigureAwait(false);

                    changeId = response.ChangeInfo.Id;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ChangeInfo.Id} {response.ChangeInfo.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListResourceRecordSets",
                $"GET {factory.ServiceUrl}/2013-04-01/hostedzone/{BareId(zoneId)}/rrset\nclient.ListResourceRecordSetsAsync(new ListResourceRecordSetsRequest {{ HostedZoneId = \"{zoneId}\" }})",
                async () =>
                {
                    ListResourceRecordSetsResponse response = await client.ListResourceRecordSetsAsync(
                        new ListResourceRecordSetsRequest { HostedZoneId = zoneId }, ct).ConfigureAwait(false);

                    string types = string.Join(", ", response.ResourceRecordSets.Select(r => $"{r.Type} {r.Name}"));
                    ResourceRecordSet? record = response.ResourceRecordSets.SingleOrDefault(r => r.Type == RRType.A && r.Name == recordName);

                    // A list that does not contain what the UPSERT wrote did not round-trip. The
                    // lede promises this page shows what floci actually answered, so it goes out
                    // red rather than a green badge over a record that is not there.
                    if (record?.ResourceRecords.SingleOrDefault()?.Value != RecordAddress)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — no A record for {recordName} with value {RecordAddress}. Found: {types}");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ResourceRecordSets.Count} record set(s): {types}\n{recordName} A {RecordTtl} → {RecordAddress}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetChange",
                $"GET {factory.ServiceUrl}/2013-04-01/change/{BareId(changeId)}\nclient.GetChangeAsync(new GetChangeRequest {{ Id = \"{changeId}\" }})",
                async () =>
                {
                    // Real Route 53 answers PENDING for up to about a minute after a change; floci
                    // answers INSYNC at once, so against the emulator the loop never waits. The
                    // 90 s budget is sized for real Route 53, which is the case it exists for.
                    GetChangeResponse response = await client.GetChangeAsync(new GetChangeRequest { Id = changeId }, ct).ConfigureAwait(false);

                    for (int attempt = 0; response.ChangeInfo.Status != ChangeStatus.INSYNC && attempt < 18; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        response = await client.GetChangeAsync(new GetChangeRequest { Id = changeId }, ct).ConfigureAwait(false);
                    }

                    if (response.ChangeInfo.Status != ChangeStatus.INSYNC)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — {response.ChangeInfo.Id} was still {response.ChangeInfo.Status} after 90 s.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ChangeInfo.Id} {response.ChangeInfo.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteHostedZone — while it holds a record",
                $"DELETE {factory.ServiceUrl}/2013-04-01/hostedzone/{BareId(zoneId)}\nclient.DeleteHostedZoneAsync(new DeleteHostedZoneRequest {{ Id = \"{zoneId}\" }})",
                async () =>
                {
                    // A zone is not deletable while it holds anything beyond its default NS and
                    // SOA records. Refusal is the passing outcome; a successful delete here means
                    // the zone (and the record) vanished, so cleanup has nothing left to do.
                    try
                    {
                        DeleteHostedZoneResponse response = await client.DeleteHostedZoneAsync(
                            new DeleteHostedZoneRequest { Id = zoneId }, ct).ConfigureAwait(false);

                        zoneId = null;
                        recordPresent = false;

                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — the zone was deleted although it still held a record.");
                    }
                    catch (HostedZoneNotEmptyException ex)
                    {
                        return $"HTTP {(int)ex.StatusCode} — {ex.ErrorCode}: {ex.Message}\n(refused, as real Route 53 does)";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ChangeResourceRecordSets — DELETE",
                $"POST {factory.ServiceUrl}/2013-04-01/hostedzone/{BareId(zoneId)}/rrset\nclient.ChangeResourceRecordSetsAsync(… Action = DELETE, {recordName} A {RecordTtl} {RecordAddress})",
                async () =>
                {
                    // DELETE has to repeat the record set exactly as it was created — name, type,
                    // TTL and values — or real Route 53 answers InvalidChangeBatch.
                    ChangeResourceRecordSetsResponse response = await client.ChangeResourceRecordSetsAsync(
                        ChangeRequest(zoneId!, ChangeAction.DELETE, recordName), ct).ConfigureAwait(false);

                    recordPresent = false;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.ChangeInfo.Id} {response.ChangeInfo.Status}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The steps it produces are yielded
            // below — an iterator may not yield from inside a finally.
            if (zoneId is not null && recordPresent)
            {
                cleanup.Add(await this.DeleteRecordAsync(client, zoneId, recordName, ct).ConfigureAwait(false));
            }

            foreach (string strayZoneId in strayZoneIds)
            {
                cleanup.Add(await this.DeleteZoneAsync(client, strayZoneId, "DeleteHostedZone — cleanup (stray zone)", ct).ConfigureAwait(false));
            }

            if (zoneId is not null)
            {
                cleanup.Add(await this.DeleteZoneAsync(client, zoneId, "DeleteHostedZone — cleanup", ct).ConfigureAwait(false));
            }
        }

        foreach (DemoStep step in cleanup)
        {
            yield return step;
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

    // Route 53 hands ids back with their resource prefix ("/hostedzone/Z…", "/change/C…") and the
    // SDK strips it before building the path, so the wire request shown beside a step has to strip
    // it too or it displays "hostedzone//hostedzone/Z…", a URL that was never sent.
    private static string? BareId(string? id) => id?[(id.LastIndexOf('/') + 1)..];

    private static ChangeResourceRecordSetsRequest ChangeRequest(string zoneId, ChangeAction action, string recordName)
        => new()
        {
            HostedZoneId = zoneId,
            ChangeBatch = new ChangeBatch
            {
                Changes =
                [
                    new Change
                    {
                        Action = action,
                        ResourceRecordSet = new ResourceRecordSet
                        {
                            Name = recordName,
                            Type = RRType.A,
                            TTL = RecordTtl,
                            ResourceRecords = [new ResourceRecord { Value = RecordAddress }],
                        },
                    },
                ],
            },
        };

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Route 53 would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the zone.
        // Catching it here would instead fabricate a "Failed" step for every remaining
        // operation, reporting the user navigating away as the emulator misbehaving.
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
    /// A record that was never written answers with <see cref="InvalidChangeBatchException"/>,
    /// which is a clean run finishing, not a cleanup failure worth showing in red. The call uses
    /// <see cref="CancellationToken.None"/> — a run that was cancelled still has a record to remove.
    /// </summary>
    private async Task<DemoStep> DeleteRecordAsync(IAmazonRoute53 client, string zoneId, string recordName, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/2013-04-01/hostedzone/{BareId(zoneId)}/rrset\nclient.ChangeResourceRecordSetsAsync(… Action = DELETE, {recordName} A {RecordTtl} {RecordAddress})";

        return await RunStepAsync("ChangeResourceRecordSets — DELETE (cleanup)", request, async () =>
        {
            try
            {
                ChangeResourceRecordSetsResponse response = await client.ChangeResourceRecordSetsAsync(
                    ChangeRequest(zoneId, ChangeAction.DELETE, recordName), CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — removed the record"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (InvalidChangeBatchException)
            {
                return "Nothing to remove — the record was never written.";
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// A zone that genuinely never got created answers with <see cref="NoSuchHostedZoneException"/>,
    /// which is a clean run finishing. <see cref="CancellationToken.None"/> for the same reason as
    /// <see cref="DeleteRecordAsync"/>.
    /// </summary>
    private async Task<DemoStep> DeleteZoneAsync(IAmazonRoute53 client, string zoneId, string title, CancellationToken ct)
    {
        string request = $"DELETE {factory.ServiceUrl}/2013-04-01/hostedzone/{BareId(zoneId)}\nclient.DeleteHostedZoneAsync(new DeleteHostedZoneRequest {{ Id = \"{zoneId}\" }})";

        return await RunStepAsync(title, request, async () =>
        {
            try
            {
                DeleteHostedZoneResponse response = await client.DeleteHostedZoneAsync(
                    new DeleteHostedZoneRequest { Id = zoneId }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — removed the zone"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (NoSuchHostedZoneException)
            {
                return "Nothing to remove — the zone was never created.";
            }
        }).ConfigureAwait(false);
    }
}
