using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using FlociLab.Core;

namespace FlociLab.Aws.Sts;

/// <summary>
/// AWS STS against floci. Ordinary AWSSDK.SecurityToken code — the only emulator-aware line in the
/// sample is in <see cref="StsClientFactory"/>. STS is the odd service in this repo in that it
/// creates nothing that persists (every credential just expires), so <see cref="RunAsync"/> has no
/// cleanup and no resource to leak; its round trip is the exchange itself — who am I, become a
/// role, prove it by signing with what came back.
/// </summary>
public sealed class StsDemo(StsClientFactory factory) : IServiceDemo
{
    public string Provider => CloudProvider.Aws;

    public string Slug => "sts";

    public string DisplayName => "STS";

    public string Category => "Security";

    public string Route => "/aws/sts";

    /// <summary>
    /// GetCallerIdentity: it needs no permission on real AWS, takes no input and changes nothing,
    /// which is exactly what a probe wants.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonSecurityTokenService client = factory.Create();
            GetCallerIdentityResponse response = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetCallerIdentity answered for {response.Arn}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonSecurityTokenService client = factory.Create();

        // Unique per run, so two runs never collide and the assumed-role ARN a step reports can be
        // traced to the run that asked for it.
        string suffix = Guid.NewGuid().ToString("N");
        string roleName = $"flocilab-sts-role-{suffix}";
        string sessionName = $"flocilab-{suffix[..8]}";

        string? account = null;
        Credentials? assumed = null;

        yield return await RunStepAsync(
            "GetCallerIdentity",
            $"POST {factory.ServiceUrl}/\nAction=GetCallerIdentity\nclient.GetCallerIdentityAsync(new GetCallerIdentityRequest())",
            async () =>
            {
                GetCallerIdentityResponse response = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct).ConfigureAwait(false);
                account = StsResponse.Require(response.Account, "GetCallerIdentity", "Account");

                return $"HTTP {(int)response.HttpStatusCode} — Account: {account}, Arn: {StsResponse.Require(response.Arn, "GetCallerIdentity", "Arn")}";
            }).ConfigureAwait(false);

        // Every later step needs the account id to build the role ARN, so a failed first step ends
        // the run rather than inventing one.
        if (account is null)
        {
            yield break;
        }

        // The role is never created. Real STS would refuse this with AccessDenied — the role has to
        // exist and trust the caller — but floci hands out credentials for any well-formed ARN
        // (docs/BLAZOR-PLAN.md §14). That is what lets the lab show the exchange without an IAM
        // detour; against real AWS, swap in a role you can actually assume.
        string roleArn = $"arn:aws:iam::{account}:role/{roleName}";

        yield return await RunStepAsync(
            "AssumeRole",
            $"POST {factory.ServiceUrl}/\nAction=AssumeRole&RoleArn={roleArn}&RoleSessionName={sessionName}&DurationSeconds=900\nclient.AssumeRoleAsync(new AssumeRoleRequest {{ RoleArn = \"{roleArn}\", RoleSessionName = \"{sessionName}\", DurationSeconds = 900 }})",
            async () =>
            {
                AssumeRoleResponse response = await client.AssumeRoleAsync(
                    new AssumeRoleRequest { RoleArn = roleArn, RoleSessionName = sessionName, DurationSeconds = 900 }, ct).ConfigureAwait(false);
                Credentials credentials = StsResponse.Require(response.Credentials, "AssumeRole", "Credentials");
                AssumedRoleUser user = StsResponse.Require(response.AssumedRoleUser, "AssumeRole", "AssumedRoleUser");
                string described = Describe(credentials);

                // Only after Describe has checked every field: credentials missing a session token
                // must fail here, not resurface as a second, murkier failure in the next step.
                assumed = credentials;

                return $"HTTP {(int)response.HttpStatusCode} — AssumedRoleUser: {user.Arn}\n{described}";
            }).ConfigureAwait(false);

        const string AssumedIdentityTitle = "GetCallerIdentity as the assumed role";
        const string AssumedIdentityRequest = "Action=GetCallerIdentity  (signed with the AssumeRole credentials and their session token)";

        if (assumed is null)
        {
            // Said out loud rather than dropped: a run that silently shrank from five steps to four
            // would hide that the one step proving the exchange never ran — which is exactly what
            // real AWS does to the made-up role above.
            yield return DemoStep.Failed(
                AssumedIdentityTitle,
                new InvalidOperationException("Skipped — AssumeRole returned no usable credentials, so there is nothing to sign with."),
                $"POST {factory.ServiceUrl}/\n{AssumedIdentityRequest}");
        }
        else
        {
            Credentials temporary = assumed;

            // The step that makes the previous one mean something: a client signed with the
            // temporary credentials has to *be* the assumed role. Checking the ARN rather than
            // just that the call succeeded is what stops a green step describing an identity the
            // emulator never actually resolved.
            yield return await RunStepAsync(
                AssumedIdentityTitle,
                $"POST {factory.ServiceUrl}/\nAction=GetCallerIdentity  (signed with {temporary.AccessKeyId} and its session token)\nnew AmazonSecurityTokenServiceClient(new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken), config).GetCallerIdentityAsync(new GetCallerIdentityRequest())",
                async () =>
                {
                    using IAmazonSecurityTokenService assumedClient = factory.Create(
                        new SessionAWSCredentials(temporary.AccessKeyId, temporary.SecretAccessKey, temporary.SessionToken));
                    GetCallerIdentityResponse response = await assumedClient.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct).ConfigureAwait(false);
                    string arn = StsResponse.Require(response.Arn, "GetCallerIdentity", "Arn");

                    if (!arn.Contains($":assumed-role/{roleName}/", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — the temporary credentials resolved to {arn}, not to the assumed role {roleName}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — Arn: {arn}";
                }).ConfigureAwait(false);
        }

        // Both of the next two need long-term IAM user keys on real AWS. Signed with anything
        // temporary — SSO, an assumed profile role, IMDS or ECS credentials — real STS answers
        // AccessDenied, so against real AWS these two steps fail unless the SDK chain resolves to
        // an access key pair (docs/BLAZOR-PLAN.md §14). floci accepts either.
        yield return await RunStepAsync(
            "GetSessionToken",
            $"POST {factory.ServiceUrl}/\nAction=GetSessionToken&DurationSeconds=900\nclient.GetSessionTokenAsync(new GetSessionTokenRequest {{ DurationSeconds = 900 }})",
            async () =>
            {
                GetSessionTokenResponse response = await client.GetSessionTokenAsync(new GetSessionTokenRequest { DurationSeconds = 900 }, ct).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode}\n{Describe(StsResponse.Require(response.Credentials, "GetSessionToken", "Credentials"))}";
            }).ConfigureAwait(false);

        yield return await RunStepAsync(
            "GetFederationToken",
            $"POST {factory.ServiceUrl}/\nAction=GetFederationToken&Name={sessionName}&DurationSeconds=900\nclient.GetFederationTokenAsync(new GetFederationTokenRequest {{ Name = \"{sessionName}\", DurationSeconds = 900 }})",
            async () =>
            {
                GetFederationTokenResponse response = await client.GetFederationTokenAsync(
                    new GetFederationTokenRequest { Name = sessionName, DurationSeconds = 900 }, ct).ConfigureAwait(false);
                FederatedUser user = StsResponse.Require(response.FederatedUser, "GetFederationToken", "FederatedUser");

                return $"HTTP {(int)response.HttpStatusCode} — FederatedUser: {user.Arn}\n{Describe(StsResponse.Require(response.Credentials, "GetFederationToken", "Credentials"))}";
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> handles only the transport-level cases; the AWS SDK
    /// reports both a 501 and floci's own error responses inside an
    /// <see cref="AmazonServiceException"/>, so they need unwrapping here. Same shape as
    /// <c>IamDemo.Classify</c>.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(DescribeError(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(DescribeError(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(DescribeError(ex), elapsed);
            }
        }

        return ProbeResult.Error(DescribeError(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real STS would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached; catching it here would instead fabricate a
        // "Failed" step for every remaining operation, reporting the user navigating away as the
        // emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// The secret access key and session token are deliberately not rendered: they are live
    /// credentials on real AWS, and this page can be pointed there (docs/BLAZOR-PLAN.md §7.9). The
    /// session token's length is shown so a missing one is still visible.
    /// </summary>
    private static string Describe(Credentials credentials)
    {
        string accessKeyId = StsResponse.Require(credentials.AccessKeyId, "STS", "Credentials.AccessKeyId");
        string sessionToken = StsResponse.Require(credentials.SessionToken, "STS", "Credentials.SessionToken");
        StsResponse.Require(credentials.SecretAccessKey, "STS", "Credentials.SecretAccessKey");

        return $"AccessKeyId: {accessKeyId}\nSecretAccessKey: (withheld)\nSessionToken: ({sessionToken.Length} characters, withheld)\nExpiration: {credentials.Expiration:O}";
    }

    private static string DescribeError(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
