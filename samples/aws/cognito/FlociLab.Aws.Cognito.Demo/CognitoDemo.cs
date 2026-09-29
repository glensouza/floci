using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Cognito;

/// <summary>
/// AWS Cognito user pools against floci. Ordinary AWSSDK.CognitoIdentityProvider code — the only
/// emulator-aware line in the sample is in <see cref="CognitoClientFactory"/>. The round trip is
/// the one every app with a login box makes: a pool, an app client, a user, a password sign-in,
/// and a token that has to resolve back to that user. Everything hangs off the pool, so cleanup is
/// a single DeleteUserPool.
/// </summary>
public sealed class CognitoDemo(CognitoClientFactory factory) : IServiceDemo
{
    public string Provider => CloudProvider.Aws;

    public string Slug => "cognito";

    public string DisplayName => "Cognito";

    public string Category => "Security";

    public string Route => "/aws/cognito";

    /// <summary>
    /// ListUserPools: read-only, and the cheapest call that proves the user-pool API is being served
    /// rather than some other service answering on the same port.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonCognitoIdentityProvider client = factory.Create();
            ListUserPoolsResponse response = await client.ListUserPoolsAsync(new ListUserPoolsRequest { MaxResults = 1 }, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListUserPools answered with HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonCognitoIdentityProvider client = factory.Create();

        // Unique per run, so two runs never collide and a pool id a step reports can be traced to
        // the run that made it.
        string suffix = Guid.NewGuid().ToString("N");
        string poolName = $"flocilab-cognito-{suffix}";

        // Generated rather than a constant so no fixed password sits in the source — the default
        // pool policy wants upper, lower, digit and symbol, and the fixed prefix supplies the first
        // and last two. Never rendered: the page can be pointed at real AWS (docs/BLAZOR-PLAN.md §7.9).
        string password = $"Fl0ci!{suffix[..16]}";
        const string UserName = "alice";
        const string SecondUserName = "bob";
        const string GroupName = "admins";

        string? poolId = null;
        string? clientId = null;
        string? accessToken = null;
        bool secondUserCreated = false;

        DemoStep? deleteStep = null;

        try
        {
            yield return await RunStepAsync(
                "CreateUserPool",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.CreateUserPool\n{{ \"PoolName\": \"{poolName}\" }}\nclient.CreateUserPoolAsync(new CreateUserPoolRequest {{ PoolName = \"{poolName}\" }})",
                async () =>
                {
                    CreateUserPoolResponse response = await client.CreateUserPoolAsync(new CreateUserPoolRequest { PoolName = poolName }, ct).ConfigureAwait(false);
                    UserPoolType userPool = CognitoResponse.Require(response.UserPool, "CreateUserPool", "UserPool");
                    poolId = CognitoResponse.Require(userPool.Id, "CreateUserPool", "UserPool.Id");

                    return $"HTTP {(int)response.HttpStatusCode} — Id: {poolId}, Arn: {userPool.Arn}";
                }).ConfigureAwait(false);

            // Everything else lives inside the pool, so a failed first step ends the run rather than
            // inventing an id to send the rest to.
            if (poolId is null)
            {
                yield break;
            }

            string pool = poolId;

            yield return await RunStepAsync(
                "CreateUserPoolClient",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.CreateUserPoolClient\n{{ \"UserPoolId\": \"{pool}\", \"ClientName\": \"flocilab-app\", \"ExplicitAuthFlows\": [\"ALLOW_USER_PASSWORD_AUTH\"] }}\nclient.CreateUserPoolClientAsync(new CreateUserPoolClientRequest {{ UserPoolId = \"{pool}\", ClientName = \"flocilab-app\", ExplicitAuthFlows = [\"ALLOW_USER_PASSWORD_AUTH\"] }})",
                async () =>
                {
                    CreateUserPoolClientResponse response = await client.CreateUserPoolClientAsync(
                        new CreateUserPoolClientRequest { UserPoolId = pool, ClientName = "flocilab-app", ExplicitAuthFlows = ["ALLOW_USER_PASSWORD_AUTH"] }, ct).ConfigureAwait(false);
                    UserPoolClientType created = CognitoResponse.Require(response.UserPoolClient, "CreateUserPoolClient", "UserPoolClient");
                    clientId = CognitoResponse.Require(created.ClientId, "CreateUserPoolClient", "UserPoolClient.ClientId");

                    return $"HTTP {(int)response.HttpStatusCode} — ClientId: {clientId}";
                }).ConfigureAwait(false);

            // SUPPRESS: no invitation email is sent — on real AWS that would go to a real address.
            // The user starts in FORCE_CHANGE_PASSWORD, which is why the next step exists.
            yield return await RunStepAsync(
                "AdminCreateUser",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.AdminCreateUser\n{{ \"UserPoolId\": \"{pool}\", \"Username\": \"{UserName}\", \"MessageAction\": \"SUPPRESS\", \"UserAttributes\": [{{ \"Name\": \"email\", \"Value\": \"{UserName}@example.com\" }}] }}\nclient.AdminCreateUserAsync(new AdminCreateUserRequest {{ UserPoolId = \"{pool}\", Username = \"{UserName}\", MessageAction = MessageActionType.SUPPRESS, ... }})",
                async () =>
                {
                    AdminCreateUserResponse response = await client.AdminCreateUserAsync(
                        new AdminCreateUserRequest
                        {
                            UserPoolId = pool,
                            Username = UserName,
                            MessageAction = MessageActionType.SUPPRESS,
                            UserAttributes = [new AttributeType { Name = "email", Value = $"{UserName}@example.com" }],
                        }, ct).ConfigureAwait(false);
                    UserType user = CognitoResponse.Require(response.User, "AdminCreateUser", "User");

                    return $"HTTP {(int)response.HttpStatusCode} — Username: {user.Username}, UserStatus: {user.UserStatus}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AdminSetUserPassword",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.AdminSetUserPassword\n{{ \"UserPoolId\": \"{pool}\", \"Username\": \"{UserName}\", \"Password\": \"(withheld)\", \"Permanent\": true }}\nclient.AdminSetUserPasswordAsync(new AdminSetUserPasswordRequest {{ UserPoolId = \"{pool}\", Username = \"{UserName}\", Password = ..., Permanent = true }})",
                async () =>
                {
                    AdminSetUserPasswordResponse response = await client.AdminSetUserPasswordAsync(
                        new AdminSetUserPasswordRequest { UserPoolId = pool, Username = UserName, Password = password, Permanent = true }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — the user is now CONFIRMED with a permanent password.";
                }).ConfigureAwait(false);

            if (clientId is null)
            {
                // Said out loud rather than dropped: a run that silently shrank would hide that the
                // sign-in — the point of the page — never ran.
                yield return DemoStep.Failed(
                    "InitiateAuth",
                    new InvalidOperationException("Skipped — CreateUserPoolClient returned no ClientId, so there is nothing to sign in against."),
                    null);
            }
            else
            {
                string app = clientId;

                yield return await RunStepAsync(
                    "InitiateAuth",
                    $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.InitiateAuth\n{{ \"ClientId\": \"{app}\", \"AuthFlow\": \"USER_PASSWORD_AUTH\", \"AuthParameters\": {{ \"USERNAME\": \"{UserName}\", \"PASSWORD\": \"(withheld)\" }} }}\nclient.InitiateAuthAsync(new InitiateAuthRequest {{ ClientId = \"{app}\", AuthFlow = AuthFlowType.USER_PASSWORD_AUTH, AuthParameters = ... }})",
                    async () =>
                    {
                        InitiateAuthResponse response = await client.InitiateAuthAsync(
                            new InitiateAuthRequest
                            {
                                ClientId = app,
                                AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
                                AuthParameters = new Dictionary<string, string> { ["USERNAME"] = UserName, ["PASSWORD"] = password },
                            }, ct).ConfigureAwait(false);
                        AuthenticationResultType result = CognitoResponse.Require(response.AuthenticationResult, "InitiateAuth", "AuthenticationResult");
                        string access = CognitoResponse.Require(result.AccessToken, "InitiateAuth", "AuthenticationResult.AccessToken");
                        string id = CognitoResponse.Require(result.IdToken, "InitiateAuth", "AuthenticationResult.IdToken");
                        string refresh = CognitoResponse.Require(result.RefreshToken, "InitiateAuth", "AuthenticationResult.RefreshToken");

                        // Only after every token has been checked: a result missing one must fail
                        // here, not resurface as a murkier failure in GetUser.
                        accessToken = access;

                        return $"HTTP {(int)response.HttpStatusCode} — TokenType: {result.TokenType}, ExpiresIn: {result.ExpiresIn}\nAccessToken: ({access.Length} characters, withheld)\nIdToken: ({id.Length} characters, withheld)\nRefreshToken: ({refresh.Length} characters, withheld)";
                    }).ConfigureAwait(false);

                // The step that makes InitiateAuth mean something: a token has to resolve back to
                // the user it was issued for. Checking the username rather than just that the call
                // succeeded is what stops a green step describing an identity nobody resolved.
                if (accessToken is null)
                {
                    yield return DemoStep.Failed(
                        "GetUser",
                        new InvalidOperationException("Skipped — InitiateAuth returned no usable access token, so there is nothing to ask about."),
                        null);
                }
                else
                {
                    string token = accessToken;

                    // Unsigned on real AWS — GetUser authenticates by the token, not by IAM — which
                    // is why the token is the only credential in the request shown.
                    yield return await RunStepAsync(
                        "GetUser",
                        $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.GetUser\n{{ \"AccessToken\": \"(withheld)\" }}\nclient.GetUserAsync(new GetUserRequest {{ AccessToken = ... }})",
                        async () =>
                        {
                            GetUserResponse response = await client.GetUserAsync(new GetUserRequest { AccessToken = token }, ct).ConfigureAwait(false);

                            if (response.Username != UserName)
                            {
                                throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — the access token resolved to '{response.Username}', not to '{UserName}'.");
                            }

                            return $"HTTP {(int)response.HttpStatusCode} — Username: {response.Username}, Attributes: {string.Join(", ", (response.UserAttributes ?? []).Select(a => a.Name))}";
                        }).ConfigureAwait(false);
                }

                // A rejection is the pass condition. It is the other half of the sign-in: a pool
                // that lets any password through would have passed every step above.
                yield return await RunStepAsync(
                    "InitiateAuth with a wrong password",
                    $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.InitiateAuth\n{{ \"ClientId\": \"{app}\", \"AuthFlow\": \"USER_PASSWORD_AUTH\", \"AuthParameters\": {{ \"USERNAME\": \"{UserName}\", \"PASSWORD\": \"(wrong)\" }} }}\nExpected: NotAuthorizedException",
                    async () =>
                    {
                        try
                        {
                            await client.InitiateAuthAsync(
                                new InitiateAuthRequest
                                {
                                    ClientId = app,
                                    AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
                                    AuthParameters = new Dictionary<string, string> { ["USERNAME"] = UserName, ["PASSWORD"] = $"not-{password}" },
                                }, ct).ConfigureAwait(false);
                        }
                        catch (NotAuthorizedException ex)
                        {
                            return $"HTTP {(int)ex.StatusCode} — NotAuthorizedException: {ex.Message}";
                        }

                        throw new InvalidOperationException("InitiateAuth accepted a password that was never set.");
                    }).ConfigureAwait(false);

                // Self-registration: the path a real sign-up form takes. UserConfirmed is false until
                // the emailed code comes back, and there is no inbox here, so the admin call stands in.
                yield return await RunStepAsync(
                    "SignUp",
                    $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.SignUp\n{{ \"ClientId\": \"{app}\", \"Username\": \"{SecondUserName}\", \"Password\": \"(withheld)\", \"UserAttributes\": [{{ \"Name\": \"email\", \"Value\": \"{SecondUserName}@example.com\" }}] }}\nclient.SignUpAsync(new SignUpRequest {{ ClientId = \"{app}\", Username = \"{SecondUserName}\", Password = ..., UserAttributes = ... }})",
                    async () =>
                    {
                        SignUpResponse response = await client.SignUpAsync(
                            new SignUpRequest
                            {
                                ClientId = app,
                                Username = SecondUserName,
                                Password = password,
                                UserAttributes = [new AttributeType { Name = "email", Value = $"{SecondUserName}@example.com" }],
                            }, ct).ConfigureAwait(false);

                        secondUserCreated = true;

                        return $"HTTP {(int)response.HttpStatusCode} — UserConfirmed: {response.UserConfirmed}, UserSub: {response.UserSub}";
                    }).ConfigureAwait(false);

                // Only once bob exists: confirming a sign-up that never happened would add a red
                // step that reads as the emulator misbehaving.
                if (secondUserCreated)
                {
                    yield return await RunStepAsync(
                        "AdminConfirmSignUp",
                        $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.AdminConfirmSignUp\n{{ \"UserPoolId\": \"{pool}\", \"Username\": \"{SecondUserName}\" }}\nclient.AdminConfirmSignUpAsync(new AdminConfirmSignUpRequest {{ UserPoolId = \"{pool}\", Username = \"{SecondUserName}\" }})",
                        async () =>
                        {
                            AdminConfirmSignUpResponse response = await client.AdminConfirmSignUpAsync(
                                new AdminConfirmSignUpRequest { UserPoolId = pool, Username = SecondUserName }, ct).ConfigureAwait(false);

                            return $"HTTP {(int)response.HttpStatusCode}";
                        }).ConfigureAwait(false);
                }
            }

            yield return await RunStepAsync(
                "CreateGroup and AdminAddUserToGroup",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.CreateGroup\n{{ \"UserPoolId\": \"{pool}\", \"GroupName\": \"{GroupName}\" }}\nX-Amz-Target: AWSCognitoIdentityProviderService.AdminAddUserToGroup\n{{ \"UserPoolId\": \"{pool}\", \"Username\": \"{UserName}\", \"GroupName\": \"{GroupName}\" }}",
                async () =>
                {
                    CreateGroupResponse group = await client.CreateGroupAsync(new CreateGroupRequest { UserPoolId = pool, GroupName = GroupName }, ct).ConfigureAwait(false);
                    AdminAddUserToGroupResponse added = await client.AdminAddUserToGroupAsync(
                        new AdminAddUserToGroupRequest { UserPoolId = pool, Username = UserName, GroupName = GroupName }, ct).ConfigureAwait(false);

                    return $"CreateGroup HTTP {(int)group.HttpStatusCode} — {CognitoResponse.Require(group.Group, "CreateGroup", "Group").GroupName}\nAdminAddUserToGroup HTTP {(int)added.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AdminListGroupsForUser",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.AdminListGroupsForUser\n{{ \"UserPoolId\": \"{pool}\", \"Username\": \"{UserName}\" }}\nclient.AdminListGroupsForUserAsync(new AdminListGroupsForUserRequest {{ UserPoolId = \"{pool}\", Username = \"{UserName}\" }})",
                async () =>
                {
                    AdminListGroupsForUserResponse response = await client.AdminListGroupsForUserAsync(
                        new AdminListGroupsForUserRequest { UserPoolId = pool, Username = UserName }, ct).ConfigureAwait(false);
                    List<string> names = (response.Groups ?? []).Select(g => g.GroupName).ToList();

                    if (!names.Contains(GroupName))
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — {UserName} is not in {GroupName}; groups: [{string.Join(", ", names)}].");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {UserName} is in: {string.Join(", ", names)}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListUsers",
                $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.ListUsers\n{{ \"UserPoolId\": \"{pool}\" }}\nclient.ListUsersAsync(new ListUsersRequest {{ UserPoolId = \"{pool}\" }})",
                async () =>
                {
                    ListUsersResponse response = await client.ListUsersAsync(new ListUsersRequest { UserPoolId = pool }, ct).ConfigureAwait(false);
                    List<UserType> users = response.Users ?? [];

                    // The expected count depends on how far the run got: alice always, bob only if
                    // his sign-up succeeded — a failed SignUp is already its own red step.
                    int expected = secondUserCreated ? 2 : 1;

                    if (users.Count != expected)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected {expected} user(s), found {users.Count}: [{string.Join(", ", users.Select(u => u.Username))}].");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", users.Select(u => $"{u.Username} ({u.UserStatus})"))}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // Deleting the pool takes the client, users and group with it (verified against floci
            // 2026-09-29). The step is yielded below — an iterator may not yield from inside a finally.
            deleteStep = poolId is null
                ? null
                : await this.DeleteUserPoolAsync(client, poolId).ConfigureAwait(false);
        }

        if (deleteStep is not null)
        {
            yield return deleteStep;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> handles only the transport-level cases; the AWS SDK
    /// reports both a 501 and floci's own error responses inside an
    /// <see cref="AmazonServiceException"/>, so they need unwrapping here. Same shape as
    /// <c>StsDemo.Classify</c>.
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
    /// Deliberately not passed the run's token: a cancelled run is exactly when the pool most needs
    /// deleting, and a cancelled token would make the delete throw before it left the process.
    /// </summary>
    private async Task<DemoStep> DeleteUserPoolAsync(IAmazonCognitoIdentityProvider client, string poolId)
        => await RunStepAsync(
            "DeleteUserPool",
            $"POST {factory.ServiceUrl}/\nX-Amz-Target: AWSCognitoIdentityProviderService.DeleteUserPool\n{{ \"UserPoolId\": \"{poolId}\" }}\nclient.DeleteUserPoolAsync(new DeleteUserPoolRequest {{ UserPoolId = \"{poolId}\" }})",
            async () =>
            {
                DeleteUserPoolResponse response = await client.DeleteUserPoolAsync(new DeleteUserPoolRequest { UserPoolId = poolId }, CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — the pool, its client, users and group are gone.";
            }).ConfigureAwait(false);

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Cognito would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still deletes the pool.
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string DescribeError(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
}
