using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using EePulse.Api.Authorization;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Dashboard;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ApiDashboardAuthorization = EePulse.Api.Authorization.DashboardAuthorization;

namespace EePulse.IntegrationTests;

public sealed class TimezonePreferenceApiTests
{
    private const string Path = Wp07DashboardContract.TimezonePreferencePath;
    private const string Actor = "2f8190f2-30ec-4bac-b4ab-79f671e561b4";
    private const string SecondActor = "3f8190f2-30ec-4bac-b4ab-79f671e561b5";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task OpenApiUsesAgentCredentialForEveryAgentCredentialOperation()
    {
        const string resultBatchPath = "/api/v1/agents/{agentId}/result-batches";
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var expectedAgentCredentialPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/v1/agents/{agentId}/heartbeat",
            "/api/v1/agents/{agentId}/configuration",
            "/api/v1/agents/{agentId}/configuration/acknowledgements",
            "/api/v1/agents/{agentId}/credentials/rotate",
            resultBatchPath
        };
        foreach (var expectedPath in expectedAgentCredentialPaths)
            Assert.True(document.RootElement.GetProperty("paths").TryGetProperty(expectedPath, out _), $"Missing generated agent path {expectedPath}.");
        var agentPaths = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.StartsWith("/api/v1/agents", StringComparison.Ordinal));

        foreach (var path in agentPaths)
        {
            foreach (var operation in path.Value.EnumerateObject().Where(property =>
                         property.Name is "get" or "post" or "put" or "delete" or "patch"))
            {
                var security = operation.Value.GetProperty("security");
                if (path.Name == "/api/v1/agents/enroll")
                {
                    Assert.Empty(security.EnumerateArray());
                    continue;
                }

                if (expectedAgentCredentialPaths.Contains(path.Name))
                {
                    Assert.Contains(security.EnumerateArray(), requirement => requirement.TryGetProperty("AgentCredential", out _));
                    Assert.DoesNotContain(security.EnumerateArray(), requirement => requirement.TryGetProperty("Bearer", out _));
                }
                else
                {
                    Assert.Contains(security.EnumerateArray(), requirement => requirement.TryGetProperty("Bearer", out _));
                }
            }
        }

        var routeEndpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText?.EndsWith("/result-batches", StringComparison.Ordinal) == true &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST", StringComparer.Ordinal) == true);
        var authorization = routeEndpoint.Metadata.GetMetadata<IAuthorizeData>();
        Assert.NotNull(authorization);
        Assert.Equal(AgentContract.CredentialAuthenticationScheme, authorization!.AuthenticationSchemes);
    }

    [Fact]
    public async Task GetPutConditionalRequestsRolesAndAuditPrivacyWorkEndToEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using (var anonymous = new HttpRequestMessage(HttpMethod.Get, Path))
        {
            var response = await client.SendAsync(anonymous, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        foreach (var role in new[] { "Viewer", "Operator", "Engineer", "Administrator", "Auditor" })
        {
            using var request = Request(HttpMethod.Get, Path, role, Actor);
            var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var denied = Request(HttpMethod.Get, Path, "Guest", Actor))
        {
            var response = await client.SendAsync(denied, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var malformedActor = Request(HttpMethod.Get, Path, "Viewer", "not-a-uuid"))
        {
            var response = await client.SendAsync(malformedActor, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var agent = new HttpRequestMessage(HttpMethod.Get, Path))
        {
            agent.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-an-agent-credential");
            var response = await client.SendAsync(agent, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var initial = await SendGetAsync<TimezonePreferenceResponse>(client, Path, "Viewer", Actor, ct);
        Assert.Null(initial.Body.Timezone);
        Assert.Equal("\"tz-0\"", initial.Body.Etag);
        Assert.Equal(initial.Body.Etag, initial.ETag);
        await AssertDatabaseStateAsync(factory, Actor, expectedTimezone: null, expectedVersion: null, expectedAuditCount: 0, ct);

        var invalidIfMatch = new[] { "W/\"tz-0\"", "*", "\"tz-01\"", "\"tz--1\"", "\"tz-9223372036854775808\"", "\"tz-1\",\"tz-0\"" };
        foreach (var value in invalidIfMatch)
        {
            using var request = PutRequest(Path, "UTC", "Viewer", Actor, value);
            var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid-if-match", await ReadProblemCodeAsync(response, ct));
        }
        using (var missingIfMatch = Request(HttpMethod.Put, Path, "Viewer", Actor, JsonContent.Create(new TimezonePreferenceRequest("UTC"))))
        {
            var response = await client.SendAsync(missingIfMatch, ct);
            Assert.Equal((HttpStatusCode)428, response.StatusCode);
            Assert.Equal("missing-if-match", await ReadProblemCodeAsync(response, ct));
        }

        Assert.Empty(await ReadTimezoneAuditSnapshotsAsync(factory, ct));
        const string hostileCorrelation = "caller-correlation-UTC-https://issuer.example-subject-1-" + Actor;
        var created = await SendPutAsync(client, "UTC", "Viewer", Actor, "\"tz-0\"", ct, hostileCorrelation);
        Assert.Equal("Etc/UTC", created.Body.Timezone);
        Assert.Equal("\"tz-1\"", created.Body.Etag);
        Assert.Equal(created.Body.Etag, created.ETag);
        await AssertDatabaseStateAsync(factory, Actor, "Etc/UTC", 1, 1, ct);
        var createdAudit = await ReadTimezoneAuditSnapshotsAsync(factory, ct);
        Assert.Single(createdAudit);
        AssertTimezoneAuditSnapshot(createdAudit[0], "create", 1, created.CorrelationId, hostileCorrelation, "UTC", Actor);

        var persisted = await SendGetAsync<TimezonePreferenceResponse>(client, Path, "Auditor", Actor, ct);
        Assert.Equal(created.Body, persisted.Body);
        using (var notModified = Request(HttpMethod.Get, Path, "Viewer", Actor))
        {
            notModified.Headers.TryAddWithoutValidation("If-None-Match", created.Body.Etag);
            var response = await client.SendAsync(notModified, ct);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Equal(created.Body.Etag, response.Headers.ETag?.Tag);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        }
        using (var listNotModified = Request(HttpMethod.Get, Path, "Viewer", Actor))
        {
            listNotModified.Headers.TryAddWithoutValidation("If-None-Match", "\"tz-9\", \"tz-1\"");
            Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(listNotModified, ct)).StatusCode);
        }
        using (var weakNotModified = Request(HttpMethod.Get, Path, "Viewer", Actor))
        {
            weakNotModified.Headers.TryAddWithoutValidation("If-None-Match", "W/\"tz-1\"");
            Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(weakNotModified, ct)).StatusCode);
        }
        using (var wildcard = Request(HttpMethod.Get, Path, "Viewer", Actor))
        {
            wildcard.Headers.TryAddWithoutValidation("If-None-Match", "*");
            Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(wildcard, ct)).StatusCode);
        }
        using (var malformedNone = Request(HttpMethod.Get, Path, "Viewer", Actor))
        {
            malformedNone.Headers.TryAddWithoutValidation("If-None-Match", "W/garbage");
            var response = await client.SendAsync(malformedNone, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid-if-none-match", await ReadProblemCodeAsync(response, ct));
        }

        var sameValue = await SendPutAsync(client, "UTC", "Viewer", Actor, created.Body.Etag, ct);
        Assert.Equal(created.Body, sameValue.Body);
        await AssertDatabaseStateAsync(factory, Actor, "Etc/UTC", 1, 1, ct);
        Assert.Equal(createdAudit, await ReadTimezoneAuditSnapshotsAsync(factory, ct));

        var invalidTimezone = await SendPutRawAsync(client, "{\"timezone\":\"Not/AZone\"}", "Viewer", Actor, created.Body.Etag, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalidTimezone.StatusCode);
        Assert.Equal("invalid-timezone", await ReadProblemCodeAsync(invalidTimezone, ct));
        var unmapped = await SendPutRawAsync(client, "{\"timezone\":\"UTC\",\"unexpected\":true}", "Viewer", Actor, created.Body.Etag, ct);
        var unmappedBody = await unmapped.Content.ReadAsStringAsync(ct);
        Assert.True(unmapped.StatusCode == HttpStatusCode.BadRequest, $"{(int)unmapped.StatusCode}: {unmappedBody}");
        foreach (var invalidPayload in new[]
        {
            "{\"timezone\":\"UTC\",\"timezone\":\"Asia/Bangkok\"}",
            "{\"timezone\":\"UTC\",\"TimeZone\":\"Asia/Bangkok\"}",
            "{\"timezone\":\"UTC\"} trailing",
            "{\"timezone\":42}"
        })
        {
            using var invalid = await SendPutRawAsync(client, invalidPayload, "Viewer", Actor, created.Body.Etag, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("invalid-json", await ReadProblemCodeAsync(invalid, ct));
        }
        using (var oversized = await SendPutRawAsync(client, "{\"timezone\":\"" + new string('x', 256) + "\"}", "Viewer", Actor, created.Body.Etag, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
            Assert.Equal("invalid-timezone", await ReadProblemCodeAsync(oversized, ct));
        }
        using (var empty = await SendPutRawAsync(client, "", "Viewer", Actor, created.Body.Etag, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.Equal("invalid-json", await ReadProblemCodeAsync(empty, ct));
        }
        await AssertDatabaseStateAsync(factory, Actor, "Etc/UTC", 1, 1, ct);
        Assert.Equal(createdAudit, await ReadTimezoneAuditSnapshotsAsync(factory, ct));

        var cleared = await SendPutAsync(client, null, "Operator", Actor, created.Body.Etag, ct);
        Assert.Null(cleared.Body.Timezone);
        Assert.Equal("\"tz-2\"", cleared.Body.Etag);
        var repeatedClear = await SendPutAsync(client, null, "Operator", Actor, cleared.Body.Etag, ct);
        Assert.Equal(cleared.Body, repeatedClear.Body);
        var reset = await SendPutAsync(client, "UTC", "Operator", Actor, cleared.Body.Etag, ct);
        Assert.Equal("Etc/UTC", reset.Body.Timezone);
        Assert.Equal("\"tz-3\"", reset.Body.Etag);
        var committedAudits = await ReadTimezoneAuditSnapshotsAsync(factory, ct);
        Assert.Equal(3, committedAudits.Count);
        Assert.Equal(committedAudits.Count, committedAudits.Select(audit => audit.Id).Distinct().Count());
        Assert.Equal(createdAudit[0], committedAudits[0]);
        AssertTimezoneAuditSnapshot(committedAudits[1], "clear", 2, cleared.CorrelationId, null, null, Actor);
        AssertTimezoneAuditSnapshot(committedAudits[2], "update", 3, reset.CorrelationId, null, "UTC", Actor);

        using (var stale = PutRequest(Path, "Asia/Tokyo", "Operator", Actor, "\"tz-0\""))
        {
            var response = await client.SendAsync(stale, ct);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal(reset.Body.Etag, response.Headers.ETag?.Tag);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("concurrency-conflict", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal(reset.Body.Etag, problem.RootElement.GetProperty("currentEtag").GetString());
        }

        await AssertDatabaseStateAsync(factory, Actor, "Etc/UTC", 3, 3, ct);
        Assert.Equal(committedAudits, await ReadTimezoneAuditSnapshotsAsync(factory, ct));

        var absentClear = await SendPutAsync(client, null, "Viewer", SecondActor, "\"tz-0\"", ct);
        Assert.Null(absentClear.Body.Timezone);
        Assert.Equal("\"tz-0\"", absentClear.Body.Etag);
        await AssertDatabaseStateAsync(factory, SecondActor, expectedTimezone: null, expectedVersion: null, expectedAuditCount: 3, ct);
        Assert.Equal(committedAudits, await ReadTimezoneAuditSnapshotsAsync(factory, ct));
    }

    [Fact]
    public async Task TimezoneRouteUsesServerCorrelationBeforeAuthorizationAndForWriteFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        const string hostile = "caller-controlled-correlation-UTC-" + Actor;

        var rejected = new HttpRequestMessage[]
        {
            new(HttpMethod.Get, Path),
            Request(HttpMethod.Get, Path, "Guest", Actor),
            Request(HttpMethod.Get, Path, "Viewer", "not-a-uuid"),
            new(HttpMethod.Get, Path)
            {
                Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-an-agent-credential") }
            }
        };

        foreach (var request in rejected)
        {
            using (request)
            {
                AddHostileCorrelationHeaders(request, hostile);
                using var response = await client.SendAsync(request, ct);
                AssertSafeRouteCorrelation(response, hostile);
            }
        }

        var created = await SendPutAsync(client, "UTC", "Operator", Actor, "\"tz-0\"", ct, hostile);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        AssertSafeCorrelationId(created.CorrelationId, hostile);
        var audit = Assert.Single(await ReadTimezoneAuditSnapshotsAsync(factory, ct));
        Assert.Equal(created.CorrelationId, audit.CorrelationId);

        var failure = await DeferredDualSqlFailure.InstallAsync(postgres.ConnectionString, "audit_events", ct);
        try
        {
            using var request = PutRequest(Path, "Asia/Bangkok", "Operator", Actor, created.ETag);
            AddHostileCorrelationHeaders(request, hostile);
            using var response = await client.SendAsync(request, ct);
            await AssertSafeWriteFailureAsync(response, hostileCorrelation: hostile, cancellationToken: ct);
        }
        finally
        {
            await failure.DisposeAsync();
        }
    }

    [Fact]
    public async Task DuplicateHumanPrincipalGetsSafeCorrelationBeforeIdentityRejection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateDuplicatePrincipalFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        AddHostileCorrelationHeaders(request, "caller-duplicate-principal-UTC-" + Actor);
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertSafeRouteCorrelation(response, "caller-duplicate-principal-UTC-" + Actor);
    }

    [Fact]
    public async Task RecognizedConflictWithFreshRereadFailureReturnsSafeWriteFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var rereadFailure = new ThrowOnConflictRereadInterceptor();
        await using var factory = CreateFactory(postgres.ConnectionString, rereadFailure);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var seed = await SendPutAsync(firstClient, "UTC", "Operator", Actor, "\"tz-0\"", ct);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        rereadFailure.Arm();

        var results = await ExecuteTerminalSafeRaceAsync(
            postgres.ConnectionString,
            [
                requestToken => SendPutForRaceAsync(firstClient, "Europe/Paris", "Operator", Actor, seed.ETag, requestToken),
                requestToken => SendPutForRaceAsync(secondClient, "America/New_York", "Operator", Actor, seed.ETag, requestToken)
            ],
            ct);
        var success = Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
        var failure = Assert.Single(results, result => result.StatusCode == HttpStatusCode.InternalServerError);
        Assert.NotNull(success.Body);
        Assert.Equal("timezone-preference-write-failed", failure.ProblemCode);
        Assert.NotNull(failure.CorrelationId);
        Assert.True(Guid.TryParseExact(failure.CorrelationId, "N", out var parsedCorrelation));
        Assert.Equal(failure.CorrelationId, parsedCorrelation.ToString("N"));
        Assert.Equal("application/problem+json", failure.ContentType);
        Assert.DoesNotContain("Npgsql", failure.ProblemBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQL", failure.ProblemBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trigger", failure.ProblemBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(rereadFailure.Triggered);
        await AssertDatabaseStateAsync(factory, Actor, success.Body!.Timezone, 2, 2, ct);
    }

    [Fact]
    public async Task UnexpectedConflictRollbackCleanupReturnsSafeWriteFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var rollbackFailure = new ThrowOnConflictRollbackInterceptor();
        await using var factory = CreateFactory(postgres.ConnectionString, rollbackFailure);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var seed = await SendPutAsync(firstClient, "UTC", "Operator", Actor, "\"tz-0\"", ct);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        var results = await ExecuteTerminalSafeRaceAsync(
            postgres.ConnectionString,
            [
                requestToken => SendPutForRaceAsync(firstClient, "Europe/Paris", "Operator", Actor, seed.ETag, requestToken),
                requestToken => SendPutForRaceAsync(secondClient, "America/New_York", "Operator", Actor, seed.ETag, requestToken)
            ],
            ct);

        var success = Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
        var failure = Assert.Single(results, result => result.StatusCode == HttpStatusCode.InternalServerError);
        Assert.NotNull(success.Body);
        Assert.Equal("timezone-preference-write-failed", failure.ProblemCode);
        Assert.Equal("application/problem+json", failure.ContentType);
        Assert.DoesNotContain("Npgsql", failure.ProblemBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQL", failure.ProblemBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rollback", failure.ProblemBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(rollbackFailure.Triggered);
        await AssertDatabaseStateAsync(factory, Actor, success.Body!.Timezone, 2, 2, ct);
    }

    [Fact]
    public async Task CallerCancellationDuringConflictWriteRemainsCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var seed = await SendPutAsync(client, "UTC", "Operator", Actor, "\"tz-0\"", ct);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        await using var gate = await PreferenceWriteGate.InstallAsync(postgres.ConnectionString, ct);
        await gate.HoldAsync(ct);
        using var request = PutRequest(Path, "Europe/Paris", "Operator", Actor, seed.ETag);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var requestTask = client.SendAsync(request, requestCancellation.Token);
        try
        {
            await gate.WaitForBlockedWritesAsync(1, ct);
            requestCancellation.Cancel();
            await gate.ReleaseAsync(CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await requestTask);
            await gate.AssertReleasedAsync(ct);
        }
        finally
        {
            requestCancellation.Cancel();
            try { await requestTask.WaitAsync(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { }
        }

        await AssertDatabaseStateAsync(factory, Actor, "Etc/UTC", 1, 1, ct);
    }

    [Fact]
    public void PrincipalIdentityResolutionRequiresExactlyOneExplicitIssuerAndSubject()
    {
        var resolver = new PrincipalIdentityResolver();
        var valid = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "https://issuer.example"), new Claim("sub", "subject-1")], "test"));
        Assert.True(resolver.TryResolve(valid, out var identity));
        Assert.Equal(new PrincipalIdentity("https://issuer.example", "subject-1"), identity);

        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "issuer")], "test")), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subject")], "test")), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "issuer"), new Claim("iss", "other"), new Claim("sub", "subject")], "test")), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Actor), new Claim(ClaimTypes.Email, "person@example.test")], "test")), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", " "), new Claim("sub", "subject")], "test")), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "issuer"), new Claim("sub", "subject")], AgentContract.CredentialAuthenticationScheme)), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "issuer\tvalue"), new Claim("sub", "subject")], "test")), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "issuer\u2028value"), new Claim("sub", "subject")], "test")), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", new string('i', TimezonePreferenceContract.MaximumIssuerLength + 1)), new Claim("sub", "subject")], "test")), out _));
        Assert.False(resolver.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "issuer"), new Claim("sub", "subject"), new Claim("sub", "subject")], "test")), out _));
    }

    [Fact]
    public async Task DashboardAuthorizationPoliciesEnforceTheFrozenRoleAndHumanIdentityMatrices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDashboardAuthorization();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var expected = new[]
        {
            new PolicyMatrix(ApiDashboardAuthorization.DashboardReadPolicy, ["Viewer", "Operator", "Engineer", "Administrator", "Auditor"]),
            new PolicyMatrix(ApiDashboardAuthorization.IncidentsReadPolicy, ["Viewer", "Operator", "Engineer", "Administrator", "Auditor"]),
            new PolicyMatrix(ApiDashboardAuthorization.IncidentsOperatePolicy, ["Operator", "Administrator"]),
            new PolicyMatrix(ApiDashboardAuthorization.AuditReadPolicy, ["Auditor", "Administrator"])
        };
        var roles = new[] { "Viewer", "Operator", "Engineer", "Administrator", "Auditor", "Unrelated" };

        foreach (var policy in expected)
        {
            foreach (var role in roles)
            {
                var result = await authorization.AuthorizeAsync(ValidHuman([role]), resource: null, policy.Name);
                Assert.Equal(policy.AllowedRoles.Contains(role, StringComparer.Ordinal), result.Succeeded);
            }

            Assert.False((await authorization.AuthorizeAsync(ValidHuman(["Unrelated"]), null, policy.Name)).Succeeded);
            Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, policy.Name)).Succeeded);
        }

        var combinations = new[]
        {
            new RoleCombination(ApiDashboardAuthorization.IncidentsReadPolicy, ["Viewer", "Engineer"], true),
            new RoleCombination(ApiDashboardAuthorization.IncidentsReadPolicy, ["Unrelated", "Auditor"], true),
            new RoleCombination(ApiDashboardAuthorization.IncidentsOperatePolicy, ["Viewer", "Engineer"], false),
            new RoleCombination(ApiDashboardAuthorization.IncidentsOperatePolicy, ["Viewer", "Operator"], true),
            new RoleCombination(ApiDashboardAuthorization.IncidentsOperatePolicy, ["Auditor", "Administrator"], true),
            new RoleCombination(ApiDashboardAuthorization.AuditReadPolicy, ["Viewer", "Engineer"], false),
            new RoleCombination(ApiDashboardAuthorization.AuditReadPolicy, ["Operator", "Auditor"], true),
            new RoleCombination(ApiDashboardAuthorization.AuditReadPolicy, ["Operator", "Administrator"], true)
        };
        foreach (var combination in combinations)
        {
            var result = await authorization.AuthorizeAsync(ValidHuman(combination.Roles), null, combination.Policy);
            Assert.Equal(combination.Allowed, result.Succeeded);
        }

        var invalidHumanFactories = new Func<IReadOnlyList<string>, ClaimsPrincipal>[]
        {
            roles => MissingClaim("iss", roles),
            roles => MissingClaim("sub", roles),
            roles => ValidHuman(roles, [new Claim("iss", "duplicate-issuer")]),
            roles => ValidHuman(roles, [new Claim("sub", "duplicate-subject")]),
            roles => HumanWithComponents("", "subject", roles),
            roles => HumanWithComponents("issuer", "", roles),
            roles => HumanWithComponents(new string('i', TimezonePreferenceContract.MaximumIssuerLength + 1), "subject", roles),
            roles => HumanWithComponents("issuer", new string('s', TimezonePreferenceContract.MaximumSubjectLength + 1), roles),
            roles => HumanWithComponents("issuer\u2028break", "subject", roles),
            roles => HumanWithComponents("issuer", "subject\tbreak", roles),
            AgentOnly
        };
        var resolver = new PrincipalIdentityResolver();
        foreach (var policy in expected)
        {
            foreach (var invalidHuman in invalidHumanFactories)
            {
                var principal = invalidHuman([policy.AllowedRoles[0]]);
                Assert.True((await authorization.AuthorizeAsync(principal, null, policy.Name)).Succeeded);
                Assert.False(resolver.TryResolve(principal, out _));
                Assert.False((await authorization.AuthorizeAsync(principal, null, ApiDashboardAuthorization.DashboardReadPolicy)).Succeeded &&
                    resolver.TryResolve(principal, out _));
            }
        }
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
    [InlineData("{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(" aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa ")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\n")]
    [InlineData("not-a-uuid")]
    public async Task InvalidDevelopmentActorsCannotObtainTimezonePreferenceIdentity(string actor)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, Path, "Viewer", actor);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request, ct)).StatusCode);
    }

    [Fact]
    public void DevelopmentActorIdentityRequiresOneCanonicalHeaderValue()
    {
        Assert.True(DevelopmentAuthenticationHandler.TryGetCanonicalPreferenceActor(
            new Microsoft.Extensions.Primitives.StringValues(Actor), out var actor));
        Assert.Equal(Actor, actor.ToString("D"));
        Assert.False(DevelopmentAuthenticationHandler.TryGetCanonicalPreferenceActor(
            new Microsoft.Extensions.Primitives.StringValues([Actor, SecondActor]), out _));
        Assert.False(DevelopmentAuthenticationHandler.TryGetCanonicalPreferenceActor(
            new Microsoft.Extensions.Primitives.StringValues(Actor + "," + SecondActor), out _));
    }

    [Fact]
    public async Task ConcurrentFirstWritesHaveOneWinnerAndNoLosingAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var firstResults = await ExecuteTerminalSafeRaceAsync(
            postgres.ConnectionString,
            [
                requestToken => SendPutForRaceAsync(firstClient, "Asia/Bangkok", "Operator", Actor, "\"tz-0\"", requestToken),
                requestToken => SendPutForRaceAsync(secondClient, "Asia/Tokyo", "Operator", Actor, "\"tz-0\"", requestToken)
            ],
            ct);
        var firstSummary = string.Join(",", firstResults.Select(result => $"{(int)result.StatusCode}:{result.ETag}:{result.Body?.Timezone}"));
        Assert.True(firstResults.Count(result => result.StatusCode == HttpStatusCode.OK) == 1, firstSummary);
        Assert.True(firstResults.Count(result => result.StatusCode == HttpStatusCode.PreconditionFailed) == 1, firstSummary);
        var winner = firstResults.Single(result => result.StatusCode == HttpStatusCode.OK);
        var loser = firstResults.Single(result => result.StatusCode == HttpStatusCode.PreconditionFailed);
        var expectedEtag = TimezonePreferenceContract.EtagForVersion(1);
        Assert.NotNull(winner.Body);
        Assert.Equal(expectedEtag, winner.Body!.Etag);
        Assert.Equal(expectedEtag, winner.ETag);
        Assert.Equal(winner.Body.Etag, winner.ETag);
        AssertConcurrencyLoser(loser, expectedEtag);
        var persisted = await ReadPreferenceSnapshotAsync(factory, Actor, ct);
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted!.Version);
        Assert.Equal(expectedEtag, TimezonePreferenceContract.EtagForVersion(persisted.Version));
        Assert.Equal(winner.Body.Timezone, persisted.Timezone);
        await AssertDatabaseStateAsync(factory, Actor, winner.Body.Timezone, 1, 1, ct);
    }

    [Fact]
    public async Task ConcurrentStaleWritesHaveOneWinnerAndNoLosingAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var seed = await SendPutAsync(firstClient, "Asia/Bangkok", "Operator", Actor, "\"tz-0\"", ct);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        var staleResults = await ExecuteTerminalSafeRaceAsync(
            postgres.ConnectionString,
            [
                requestToken => SendPutForRaceAsync(firstClient, "Europe/Paris", "Operator", Actor, seed.ETag, requestToken),
                requestToken => SendPutForRaceAsync(secondClient, "America/New_York", "Operator", Actor, seed.ETag, requestToken)
            ],
            ct);
        var staleSummary = string.Join(",", staleResults.Select(result => $"{(int)result.StatusCode}:{result.ETag}:{result.Body?.Timezone}"));
        Assert.True(staleResults.Count(result => result.StatusCode == HttpStatusCode.OK) == 1, staleSummary);
        Assert.True(staleResults.Count(result => result.StatusCode == HttpStatusCode.PreconditionFailed) == 1, staleSummary);
        var winner = staleResults.Single(result => result.StatusCode == HttpStatusCode.OK);
        var loser = staleResults.Single(result => result.StatusCode == HttpStatusCode.PreconditionFailed);
        var expectedEtag = TimezonePreferenceContract.EtagForVersion(2);
        Assert.NotNull(winner.Body);
        Assert.Equal(expectedEtag, winner.Body!.Etag);
        Assert.Equal(expectedEtag, winner.ETag);
        Assert.Equal(winner.Body.Etag, winner.ETag);
        AssertConcurrencyLoser(loser, expectedEtag);
        var persisted = await ReadPreferenceSnapshotAsync(factory, Actor, ct);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted!.Version);
        Assert.Equal(expectedEtag, TimezonePreferenceContract.EtagForVersion(persisted.Version));
        Assert.Equal(winner.Body.Timezone, persisted.Timezone);
        await AssertDatabaseStateAsync(factory, Actor, winner.Body.Timezone, 2, 2, ct);
    }

    [Fact]
    public async Task DeferredRaceOwnershipDrainSettlesTransferredResourcesExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = DeferredRaceOwnership.Count;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var gate = await PreferenceWriteGate.InstallAsync(postgres.ConnectionString, ct);
        await gate.HoldAsync(ct);
        var batch = RaceWriteBatch.Start(
            [
                requestToken => SendPutForRaceAsync(firstClient, "Asia/Bangkok", "Operator", Actor, "\"tz-0\"", requestToken),
                requestToken => SendPutForRaceAsync(secondClient, "Asia/Tokyo", "Operator", Actor, "\"tz-0\"", requestToken)
            ],
            ct);
        await gate.WaitForBlockedWritesAsync(2, ct);

        var triggerName = gate.TriggerName;
        var functionName = gate.FunctionName;
        DeferredRaceOwnership.Retain(gate, batch);
        var report = await DeferredRaceOwnership.DrainAsync(ct);

        Assert.Equal(1, report.ObservedEntries);
        Assert.Equal(baseline, report.RemainingEntries);
        Assert.Empty(report.Failures);
        Assert.True(batch.AreTerminal);
        Assert.Equal(1, batch.DisposeCount);
        Assert.Equal(1, gate.DisposeCount);
        Assert.Empty(DeferredRaceOwnership.DiagnosticSnapshot);
        await AssertGateObjectsAbsentAsync(postgres.ConnectionString, triggerName, functionName, ct);
        var persisted = Assert.IsType<TimezonePreferenceSnapshot>(await ReadPreferenceSnapshotAsync(factory, Actor, ct));
        Assert.Equal(DevelopmentAuthenticationHandler.DevelopmentIssuer, persisted.Issuer);
        Assert.Equal(Actor, persisted.Subject);
        Assert.True(persisted.Timezone is "Asia/Bangkok" or "Asia/Tokyo");
        Assert.Equal(1, persisted.Version);
        Assert.Equal(persisted.CreatedAt, persisted.UpdatedAt);
        await AssertDatabaseStateAsync(factory, Actor, persisted.Timezone, 1, 1, ct);
    }

    [Fact]
    public async Task PreferenceAndAuditRollBackTogetherWhenAuditInsertFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var failure = await DeferredDualSqlFailure.InstallAsync(postgres.ConnectionString, "audit_events", ct);
        var before = await ReadTimezoneAuditSnapshotsAsync(factory, ct);
        var beforePreference = await ReadPreferenceSnapshotAsync(factory, Actor, ct);

        try
        {
            using var request = PutRequest(Path, "UTC", "Operator", Actor, "\"tz-0\"");
            using var response = await client.SendAsync(request, ct);
            await AssertSafeWriteFailureAsync(response, ct);
            await AssertDatabaseStateAsync(factory, Actor, expectedTimezone: null, expectedVersion: null, expectedAuditCount: 0, ct);
            await failure.AssertBothSqlOperationsExecutedAsync(ct);
            Assert.Equal(before, await ReadTimezoneAuditSnapshotsAsync(factory, ct));
            Assert.Equal(beforePreference, await ReadPreferenceSnapshotAsync(factory, Actor, ct));
        }
        finally { await failure.DisposeAsync(); }

        var retry = await SendPutAsync(client, "UTC", "Operator", Actor, "\"tz-0\"", ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var replay = await SendPutAsync(client, "UTC", "Operator", Actor, retry.ETag, ct);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await AssertDatabaseStateAsync(factory, Actor, "Etc/UTC", 1, 1, ct);
        var committed = await ReadTimezoneAuditSnapshotsAsync(factory, ct);
        Assert.Single(committed);
        AssertTimezoneAuditSnapshot(committed[0], "create", 1, retry.CorrelationId, null, "UTC", Actor);
    }

    [Fact]
    public async Task PreferenceFlushFailureRollsBackAuditAndRetryIsExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var failure = await DeferredDualSqlFailure.InstallAsync(postgres.ConnectionString, "user_timezone_preferences", ct);
        var before = await ReadTimezoneAuditSnapshotsAsync(factory, ct);
        var beforePreference = await ReadPreferenceSnapshotAsync(factory, Actor, ct);
        try
        {
            using var request = PutRequest(Path, "UTC", "Operator", Actor, "\"tz-0\"");
            using var response = await client.SendAsync(request, ct);
            await AssertSafeWriteFailureAsync(response, ct);
            await AssertDatabaseStateAsync(factory, Actor, expectedTimezone: null, expectedVersion: null, expectedAuditCount: 0, ct);
            await failure.AssertBothSqlOperationsExecutedAsync(ct);
            Assert.Equal(before, await ReadTimezoneAuditSnapshotsAsync(factory, ct));
            Assert.Equal(beforePreference, await ReadPreferenceSnapshotAsync(factory, Actor, ct));
        }
        finally { await failure.DisposeAsync(); }
        var retry = await SendPutAsync(client, "UTC", "Operator", Actor, "\"tz-0\"", ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        _ = await SendPutAsync(client, "UTC", "Operator", Actor, retry.ETag, ct);
        await AssertDatabaseStateAsync(factory, Actor, "Etc/UTC", 1, 1, ct);
        var committed = await ReadTimezoneAuditSnapshotsAsync(factory, ct);
        Assert.Single(committed);
        AssertTimezoneAuditSnapshot(committed[0], "create", 1, retry.CorrelationId, null, "UTC", Actor);
    }

    [Fact]
    public async Task PreferenceAggregateSnapshotTracksIdentityTimestampsAndExactVersionProgression()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var started = DateTimeOffset.UtcNow;

        Assert.Null(await ReadPreferenceSnapshotAsync(factory, Actor, ct));
        var created = await SendPutAsync(client, "UTC", "Operator", Actor, "\"tz-0\"", ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdSnapshot = Assert.IsType<TimezonePreferenceSnapshot>(await ReadPreferenceSnapshotAsync(factory, Actor, ct));
        Assert.Equal(DevelopmentAuthenticationHandler.DevelopmentIssuer, createdSnapshot.Issuer);
        Assert.Equal(Actor, createdSnapshot.Subject);
        Assert.Equal("Etc/UTC", createdSnapshot.Timezone);
        Assert.Equal(1, createdSnapshot.Version);
        Assert.Equal(createdSnapshot.CreatedAt, createdSnapshot.UpdatedAt);
        AssertUtcAndRecent(createdSnapshot.CreatedAt, started);

        var sameValue = await SendPutAsync(client, "UTC", "Operator", Actor, created.ETag, ct);
        Assert.Equal(HttpStatusCode.OK, sameValue.StatusCode);
        Assert.Equal(createdSnapshot, await ReadPreferenceSnapshotAsync(factory, Actor, ct));

        var cleared = await SendPutAsync(client, null, "Operator", Actor, created.ETag, ct);
        var clearedSnapshot = Assert.IsType<TimezonePreferenceSnapshot>(await ReadPreferenceSnapshotAsync(factory, Actor, ct));
        Assert.Equal(createdSnapshot.Issuer, clearedSnapshot.Issuer);
        Assert.Equal(createdSnapshot.Subject, clearedSnapshot.Subject);
        Assert.Null(clearedSnapshot.Timezone);
        Assert.Equal(2, clearedSnapshot.Version);
        Assert.Equal(createdSnapshot.CreatedAt, clearedSnapshot.CreatedAt);
        Assert.True(clearedSnapshot.UpdatedAt >= createdSnapshot.UpdatedAt);
        AssertUtcAndRecent(clearedSnapshot.UpdatedAt, started);

        var repeatedClear = await SendPutAsync(client, null, "Operator", Actor, cleared.ETag, ct);
        Assert.Equal(HttpStatusCode.OK, repeatedClear.StatusCode);
        Assert.Equal(clearedSnapshot, await ReadPreferenceSnapshotAsync(factory, Actor, ct));

        var reset = await SendPutAsync(client, "UTC", "Operator", Actor, cleared.ETag, ct);
        var resetSnapshot = Assert.IsType<TimezonePreferenceSnapshot>(await ReadPreferenceSnapshotAsync(factory, Actor, ct));
        Assert.Equal(createdSnapshot.Issuer, resetSnapshot.Issuer);
        Assert.Equal(createdSnapshot.Subject, resetSnapshot.Subject);
        Assert.Equal("Etc/UTC", resetSnapshot.Timezone);
        Assert.Equal(3, resetSnapshot.Version);
        Assert.Equal(createdSnapshot.CreatedAt, resetSnapshot.CreatedAt);
        Assert.True(resetSnapshot.UpdatedAt >= clearedSnapshot.UpdatedAt);
        AssertUtcAndRecent(resetSnapshot.UpdatedAt, started);
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString, params IInterceptor[] interceptors) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            if (interceptors.Length == 0) return;
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<EePulseDbContext>>();
                services.AddDbContext<EePulseDbContext>(options => options
                    .UseNpgsql(connectionString)
                    .AddInterceptors(interceptors));
            });
        });

    private static WebApplicationFactory<Program> CreateDuplicatePrincipalFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.ConfigureTestServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = DuplicatePrincipalAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = DuplicatePrincipalAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, DuplicatePrincipalAuthenticationHandler>(
                    DuplicatePrincipalAuthenticationHandler.SchemeName, _ => { }));
        });

    private static HttpRequestMessage Request(HttpMethod method, string path, string role, string? actor, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation(DevelopmentAuthenticationHandler.RoleHeader, role);
        if (actor is not null) request.Headers.TryAddWithoutValidation(DevelopmentAuthenticationHandler.ActorHeader, actor);
        return request;
    }

    private static ClaimsPrincipal ValidHuman(IEnumerable<string> roles, IEnumerable<Claim>? additionalClaims = null) =>
        HumanWithComponents("https://issuer.example", "subject-1", roles, additionalClaims);

    private static ClaimsPrincipal MissingClaim(string type, IEnumerable<string> roles)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role)).ToList();
        if (type != "iss") claims.Add(new Claim("iss", "https://issuer.example"));
        if (type != "sub") claims.Add(new Claim("sub", "subject-1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static ClaimsPrincipal HumanWithComponents(string issuer, string subject, IEnumerable<string> roles, IEnumerable<Claim>? additionalClaims = null)
    {
        var claims = new List<Claim> { new("iss", issuer), new("sub", subject) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        if (additionalClaims is not null) claims.AddRange(additionalClaims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static ClaimsPrincipal AgentOnly(IEnumerable<string> roles)
    {
        var claims = new List<Claim> { new("iss", "https://issuer.example"), new("sub", "subject-1") };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AgentContract.CredentialAuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    private static HttpRequestMessage PutRequest(string path, string? timezone, string role, string actor, string etag)
    {
        var request = Request(HttpMethod.Put, path, role, actor, JsonContent.Create(new TimezonePreferenceRequest(timezone)));
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    private static async Task<(HttpStatusCode StatusCode, string ETag, T Body)> SendGetAsync<T>(HttpClient client, string path, string role, string actor, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path, role, actor);
        var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
        return (response.StatusCode, response.Headers.ETag?.Tag ?? string.Empty, body);
    }

    private static async Task<(HttpStatusCode StatusCode, string ETag, TimezonePreferenceResponse Body, string CorrelationId)> SendPutAsync(HttpClient client, string? timezone, string role, string actor, string etag, CancellationToken ct, string? callerCorrelation = null)
    {
        using var request = PutRequest(Path, timezone, role, actor, etag);
        if (callerCorrelation is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", callerCorrelation);
            request.Headers.TryAddWithoutValidation("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
            request.Headers.TryAddWithoutValidation("tracestate", "caller=" + callerCorrelation);
        }
        using var response = await client.SendAsync(request, ct);
        var body = response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<TimezonePreferenceResponse>(cancellationToken: ct))! : new TimezonePreferenceResponse(null, response.Headers.ETag?.Tag ?? string.Empty);
        return (response.StatusCode, response.Headers.ETag?.Tag ?? string.Empty, body, response.Headers.GetValues("X-Correlation-ID").Single());
    }

    private static async Task<RacePutResponse> SendPutForRaceAsync(HttpClient client, string? timezone, string role, string actor, string etag, CancellationToken ct)
    {
        using var request = PutRequest(Path, timezone, role, actor, etag);
        using var response = await client.SendAsync(request, ct);
        var responseEtag = response.Headers.ETag?.Tag ?? string.Empty;
        var content = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return new(response.StatusCode, responseEtag, JsonSerializer.Deserialize<TimezonePreferenceResponse>(content, WebJson), null, null, null);

        using var problem = JsonDocument.Parse(content);
        return new(
            response.StatusCode,
            responseEtag,
            null,
            problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null,
            problem.RootElement.TryGetProperty("currentEtag", out var currentEtag) ? currentEtag.GetString() : null,
            problem.RootElement.TryGetProperty("correlationId", out var correlationId) ? correlationId.GetString() : null,
            content,
            response.Content.Headers.ContentType?.MediaType);
    }

    private static void AssertConcurrencyLoser(RacePutResponse loser, string expectedEtag)
    {
        Assert.Equal(HttpStatusCode.PreconditionFailed, loser.StatusCode);
        Assert.Equal(TimezonePreferenceContract.ConcurrencyConflictCode, loser.ProblemCode);
        Assert.Equal(expectedEtag, loser.ETag);
        Assert.Equal(expectedEtag, loser.CurrentEtag);
        Assert.Equal(loser.ETag, loser.CurrentEtag);
        Assert.NotNull(loser.CorrelationId);
    }

    private static async Task<HttpResponseMessage> SendPutRawAsync(HttpClient client, string json, string role, string actor, string etag, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put, Path, role, actor, new StringContent(json, Encoding.UTF8, "application/json"));
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request, ct);
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task AssertSafeWriteFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("timezone-preference-write-failed", problem.RootElement.GetProperty("code").GetString());
        var headerCorrelationId = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.True(Guid.TryParseExact(headerCorrelationId, "N", out var correlationId));
        Assert.Equal(headerCorrelationId, correlationId.ToString("N"));
        Assert.Equal(headerCorrelationId, problem.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wp07", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQL", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertSafeWriteFailureAsync(HttpResponseMessage response, string hostileCorrelation, CancellationToken cancellationToken)
    {
        await AssertSafeWriteFailureAsync(response, cancellationToken);
        AssertSafeRouteCorrelation(response, hostileCorrelation);
    }

    private static void AddHostileCorrelationHeaders(HttpRequestMessage request, string hostileCorrelation)
    {
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", hostileCorrelation);
        request.Headers.TryAddWithoutValidation("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        request.Headers.TryAddWithoutValidation("tracestate", "caller=" + hostileCorrelation);
    }

    private static void AssertSafeRouteCorrelation(HttpResponseMessage response, string hostileCorrelation)
    {
        var value = response.Headers.GetValues("X-Correlation-ID").Single();
        AssertSafeCorrelationId(value, hostileCorrelation);
        Assert.DoesNotContain(hostileCorrelation, response.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSafeCorrelationId(string value, string hostileCorrelation)
    {
        Assert.True(Guid.TryParseExact(value, "N", out var parsed));
        Assert.Equal(value, parsed.ToString("N"));
        Assert.NotEqual(hostileCorrelation, value);
        Assert.DoesNotContain(hostileCorrelation, value, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DropFailureTriggerAsync(string connectionString, string trigger, string function, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var quote = new NpgsqlCommandBuilder();
        var table = trigger.Contains("audit", StringComparison.Ordinal) ? "audit_events" : "user_timezone_preferences";
        await using var command = new NpgsqlCommand($"DROP TRIGGER IF EXISTS {quote.QuoteIdentifier(trigger)} ON {table}; DROP FUNCTION IF EXISTS {quote.QuoteIdentifier(function)}();", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AssertDatabaseStateAsync(WebApplicationFactory<Program> factory, string actor, string? expectedTimezone, long? expectedVersion, int expectedAuditCount, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var canonicalSubject = actor.ToLowerInvariant();
        var row = await db.UserTimezonePreferences.AsNoTracking().SingleOrDefaultAsync(x => x.Subject == canonicalSubject, ct);
        if (expectedVersion is null) Assert.Null(row);
        else
        {
            Assert.NotNull(row);
            Assert.Equal(expectedTimezone, row!.Timezone);
            Assert.Equal(expectedVersion, row.Version);
        }
        Assert.Equal(expectedAuditCount, await db.AuditEvents.CountAsync(x => x.Action == "dashboard.timezone-preference.changed", ct));
    }

    private static async Task AssertGateObjectsAbsentAsync(string connectionString, string triggerName, string functionName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = @trigger), EXISTS (SELECT 1 FROM pg_proc WHERE proname = @function)", connection);
        command.Parameters.AddWithValue("trigger", triggerName);
        command.Parameters.AddWithValue("function", functionName);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    private static async Task<TimezonePreferenceSnapshot?> ReadPreferenceSnapshotAsync(WebApplicationFactory<Program> factory, string actor, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var canonicalSubject = actor.ToLowerInvariant();
        var row = await db.UserTimezonePreferences.AsNoTracking().SingleOrDefaultAsync(x => x.Subject == canonicalSubject, ct);
        return row is null ? null : new(row.Issuer, row.Subject, row.Timezone, row.Version, row.CreatedAt, row.UpdatedAt);
    }

    private static void AssertUtcAndRecent(DateTimeOffset timestamp, DateTimeOffset started)
    {
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.InRange(timestamp, started.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private static async Task<IReadOnlyList<AuditEventSnapshot>> ReadTimezoneAuditSnapshotsAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        return (await db.AuditEvents.AsNoTracking()
            .Where(x => x.Action == "dashboard.timezone-preference.changed")
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
            .ToListAsync(ct))
            .Select(audit => new AuditEventSnapshot(
                audit.Id, audit.ActorId, audit.Action, audit.EntityType, audit.EntityId, audit.BeforeJson,
                CanonicalJson(audit.AfterJson), audit.CorrelationId, audit.OccurredAt, audit.SourceIp))
            .ToArray();
    }

    private static void AssertTimezoneAuditSnapshot(
        AuditEventSnapshot snapshot, string operation, long version, string correlationId,
        string? hostileCorrelation, string? submittedTimezone, string actor)
    {
        Assert.NotEqual(Guid.Empty, snapshot.Id);
        Assert.Null(snapshot.ActorId);
        Assert.Equal("dashboard.timezone-preference.changed", snapshot.Action);
        Assert.Equal("TimezonePreference", snapshot.EntityType);
        Assert.Null(snapshot.EntityId);
        Assert.Null(snapshot.BeforeJson);
        Assert.Null(snapshot.SourceIp);
        Assert.True(snapshot.OccurredAt.Offset == TimeSpan.Zero);
        Assert.True(snapshot.OccurredAt <= DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(Guid.TryParseExact(snapshot.CorrelationId, "N", out var parsedCorrelation));
        Assert.Equal(snapshot.CorrelationId, parsedCorrelation.ToString("N"));
        Assert.Equal(correlationId, snapshot.CorrelationId);
        Assert.Equal(CanonicalJson($"{{\"operation\":\"{operation}\",\"version\":{version}}}"), snapshot.AfterJson);

        var forbidden = new[]
        {
            hostileCorrelation, "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", submittedTimezone,
            "Etc/UTC", DevelopmentAuthenticationHandler.DevelopmentIssuer, "subject-1", actor,
            "Authorization", "Bearer", "traceparent", "tracestate", "token", "credential"
        }.Where(value => !string.IsNullOrEmpty(value));
        foreach (var value in forbidden)
        {
            foreach (var scalar in snapshot.StringScalars())
                Assert.DoesNotContain(value!, scalar, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? CanonicalJson(string? json)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        var properties = document.RootElement.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"\"{property.Name}\":{property.Value.GetRawText()}");
        return "{" + string.Join(",", properties) + "}";
    }

    private static async Task<RacePutResponse[]> ExecuteTerminalSafeRaceAsync(
        string connectionString,
        IReadOnlyList<Func<CancellationToken, Task<RacePutResponse>>> startRequests,
        CancellationToken testCancellationToken)
    {
        PreferenceWriteGate? gate = null;
        RaceWriteBatch? batch = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        RacePutResponse[]? results = null;

        try
        {
            gate = await PreferenceWriteGate.InstallAsync(connectionString, testCancellationToken);
            await gate.HoldAsync(testCancellationToken);
            batch = RaceWriteBatch.Start(startRequests, testCancellationToken);
            await gate.WaitForBlockedWritesAsync(startRequests.Count, testCancellationToken);
            await gate.ReleaseAsync(CancellationToken.None);
            results = await batch.ObserveResultsAsync();
            await gate.AssertReleasedAsync(testCancellationToken);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        var terminal = batch is null;
        if (batch is not null)
        {
            try
            {
                terminal = await batch.EnsureTerminalAsync(gate);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
                terminal = batch.AreTerminal;
            }
        }

        if (gate is not null)
        {
            if (terminal)
            {
                try
                {
                    await gate.DisposeAsync();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
            else
            {
                DeferredRaceOwnership.Retain(gate, batch!);
                cleanupFailures.Add(new TimeoutException($"Race ownership transferred for active drain: {batch!.DescribeForDiagnostics()}"));
                gate = null;
                batch = null;
            }
        }

        if (batch is not null)
        {
            try
            {
                batch.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0)
                primaryFailure.Data["timezonePreferenceRaceCleanupFailures"] = cleanupFailures.ToArray();
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (cleanupFailures.Count == 1) ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        if (cleanupFailures.Count > 1) throw new AggregateException(cleanupFailures);
        return results!;
    }

    private sealed class DeferredDualSqlFailure : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _target;
        private DeferredDualSqlFailure(string connectionString, string target) { _connectionString = connectionString; _target = target; }
        public static async Task<DeferredDualSqlFailure> InstallAsync(string connectionString, string target, CancellationToken ct)
        {
            var result = new DeferredDualSqlFailure(connectionString, target);
            await using var c = new NpgsqlConnection(connectionString); await c.OpenAsync(ct);
            var sql = $"CREATE SEQUENCE wp07_pref_seen; CREATE SEQUENCE wp07_audit_seen; " +
                "CREATE FUNCTION wp07_pref_seen_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('wp07_pref_seen'); RETURN NEW; END; $$; " +
                "CREATE FUNCTION wp07_audit_seen_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('wp07_audit_seen'); RETURN NEW; END; $$; " +
                "CREATE FUNCTION wp07_deferred_fail_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'wp07 deferred dual sql failure'; END; $$; " +
                "CREATE TRIGGER wp07_pref_seen_tr AFTER INSERT OR UPDATE ON user_timezone_preferences FOR EACH ROW EXECUTE FUNCTION wp07_pref_seen_fn(); " +
                "CREATE TRIGGER wp07_audit_seen_tr AFTER INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION wp07_audit_seen_fn(); " +
                $"CREATE CONSTRAINT TRIGGER wp07_deferred_fail_tr AFTER INSERT OR UPDATE ON {target} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION wp07_deferred_fail_fn();";
            await using var cmd = new NpgsqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(ct); return result;
        }
        public async Task AssertBothSqlOperationsExecutedAsync(CancellationToken ct)
        {
            await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT (SELECT last_value FROM wp07_pref_seen), (SELECT last_value FROM wp07_audit_seen)", c);
            await using var r = await cmd.ExecuteReaderAsync(ct); Assert.True(await r.ReadAsync(ct)); Assert.True(r.GetInt64(0) >= 1); Assert.True(r.GetInt64(1) >= 1);
        }
        public async ValueTask DisposeAsync()
        {
            await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP TRIGGER IF EXISTS wp07_deferred_fail_tr ON {_target}; DROP TRIGGER IF EXISTS wp07_pref_seen_tr ON user_timezone_preferences; DROP TRIGGER IF EXISTS wp07_audit_seen_tr ON audit_events; DROP FUNCTION IF EXISTS wp07_deferred_fail_fn(); DROP FUNCTION IF EXISTS wp07_pref_seen_fn(); DROP FUNCTION IF EXISTS wp07_audit_seen_fn(); DROP SEQUENCE IF EXISTS wp07_pref_seen; DROP SEQUENCE IF EXISTS wp07_audit_seen;", c); await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class ThrowOnConflictRereadInterceptor : DbCommandInterceptor
    {
        private int _armed;
        private int _preferenceReads;
        private int _triggered;

        public bool Triggered => Volatile.Read(ref _triggered) == 1;

        public void Arm()
        {
            Volatile.Write(ref _preferenceReads, 0);
            Volatile.Write(ref _armed, 1);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                command.CommandText.Contains("FROM user_timezone_preferences", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("LIMIT 2", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref _preferenceReads) == 3)
            {
                Volatile.Write(ref _triggered, 1);
                return ValueTask.FromException<InterceptionResult<DbDataReader>>(
                    new InvalidOperationException("test conflict fresh reread failure"));
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class DuplicatePrincipalAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "WP07DuplicatePrincipal";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.Role, "Viewer"),
                new Claim("iss", "https://issuer.example"),
                new Claim("iss", "https://issuer.example"),
                new Claim("sub", "subject-1")
            ], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class ThrowOnConflictRollbackInterceptor : DbTransactionInterceptor
    {
        private int _triggered;

        public bool Triggered => Volatile.Read(ref _triggered) == 1;

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _triggered, 1);
            return ValueTask.FromException<InterceptionResult>(new InvalidOperationException("test conflict rollback cleanup failure"));
        }
    }

    private sealed class PreferenceWriteGate : IAsyncDisposable
    {
        private const long LockKey = 7_070_701;
        private readonly string _connectionString;
        private readonly string _functionName;
        private readonly string _triggerName;
        private NpgsqlConnection? _holder;
        private NpgsqlTransaction? _transaction;
        private int _disposed;
        private int _disposeCount;

        private PreferenceWriteGate(string connectionString, string functionName, string triggerName)
        {
            _connectionString = connectionString;
            _functionName = functionName;
            _triggerName = triggerName;
        }

        public string FunctionName => _functionName;
        public string TriggerName => _triggerName;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public static async Task<PreferenceWriteGate> InstallAsync(string connectionString, CancellationToken ct)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var gate = new PreferenceWriteGate(connectionString, "wp07_timezone_gate_fn_" + suffix, "wp07_timezone_gate_tr_" + suffix);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            var quote = new NpgsqlCommandBuilder();
            var function = quote.QuoteIdentifier(gate._functionName);
            var trigger = quote.QuoteIdentifier(gate._triggerName);
            await using var command = new NpgsqlCommand($"CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_xact_lock({LockKey}); RETURN NEW; END; $$; CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE ON user_timezone_preferences FOR EACH ROW EXECUTE FUNCTION {function}();", connection);
            await command.ExecuteNonQueryAsync(ct);
            return gate;
        }

        public async Task HoldAsync(CancellationToken ct)
        {
            _holder = new NpgsqlConnection(_connectionString);
            await _holder.OpenAsync(ct);
            _transaction = await _holder.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LockKey});", _holder, _transaction);
            await command.ExecuteNonQueryAsync(ct);
        }

        public async Task WaitForBlockedWritesAsync(int expectedCount, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(ct);
                await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%user_timezone_preferences%'", connection);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) >= expectedCount) return;
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
            throw new TimeoutException($"Expected {expectedCount} timezone writes to reach the advisory-lock boundary.");
        }

        public async Task ReleaseAsync(CancellationToken ct)
        {
            if (_transaction is not null) await _transaction.RollbackAsync(ct);
            if (_transaction is not null) await _transaction.DisposeAsync();
            if (_holder is not null) await _holder.DisposeAsync();
            _transaction = null;
            _holder = null;
        }

        public async Task AssertReleasedAsync(CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockKey});", connection);
            Assert.True((bool)(await command.ExecuteScalarAsync(ct))!);
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LockKey});", connection);
            Assert.True((bool)(await release.ExecuteScalarAsync(ct))!);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Increment(ref _disposeCount);
            await ReleaseAsync(CancellationToken.None);
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var quote = new NpgsqlCommandBuilder();
            await using var command = new NpgsqlCommand($"DROP TRIGGER IF EXISTS {quote.QuoteIdentifier(_triggerName)} ON user_timezone_preferences; DROP FUNCTION IF EXISTS {quote.QuoteIdentifier(_functionName)}();", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class RaceWriteBatch : IDisposable
    {
        private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(15);
        private readonly CancellationTokenSource[] _requestCancellations;
        private readonly Task<RacePutResponse>[] _requests;
        private bool _cleanupInitiated;
        private int _disposeCount;

        private RaceWriteBatch(
            CancellationTokenSource[] requestCancellations,
            Task<RacePutResponse>[] requests)
        {
            _requestCancellations = requestCancellations;
            _requests = requests;
        }

        public static RaceWriteBatch Start(
            IReadOnlyList<Func<CancellationToken, Task<RacePutResponse>>> startRequests,
            CancellationToken testCancellationToken)
        {
            var cancellations = startRequests.Select(_ => CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken)).ToArray();
            try
            {
                var requests = startRequests.Select((start, index) => start(cancellations[index].Token)).ToArray();
                return new RaceWriteBatch(cancellations, requests);
            }
            catch
            {
                foreach (var cancellation in cancellations) cancellation.Dispose();
                throw;
            }
        }

        public async Task<RacePutResponse[]> ObserveResultsAsync()
        {
            if (!await WaitForTerminalAsync())
                throw new TimeoutException($"Race requests did not reach a terminal state after gate release: {DescribeRequests()}");
            return await Task.WhenAll(_requests);
        }

        public async Task<bool> EnsureTerminalAsync(PreferenceWriteGate? gate)
        {
            var failures = new List<Exception>();
            if (gate is not null)
            {
                try
                {
                    await gate.ReleaseAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (!await WaitForTerminalAsync())
            {
                _cleanupInitiated = true;
                foreach (var cancellation in _requestCancellations) cancellation.Cancel();
                if (!await WaitForTerminalAsync())
                {
                    failures.Add(new TimeoutException($"Race requests did not reach a terminal state after cancellation: {DescribeRequests()}"));
                    ThrowCleanupFailures(failures);
                    return false;
                }
            }

            foreach (var request in _requests)
            {
                if (request.IsCanceled && _cleanupInitiated) continue;
                if (request.IsCanceled) failures.Add(new InvalidOperationException("A race request was canceled before cleanup initiated cancellation."));
                else if (request.IsFaulted) failures.Add(new InvalidOperationException($"A race request faulted: {request.Exception}", request.Exception));
            }

            ThrowCleanupFailures(failures);
            return true;
        }

        public void Dispose()
        {
            if (_requests.Any(request => !request.IsCompleted))
                throw new InvalidOperationException("Race request ownership cannot be disposed before every request is terminal.");
            if (Interlocked.Exchange(ref _disposeCount, 1) != 0) return;
            foreach (var cancellation in _requestCancellations) cancellation.Dispose();
        }

        public bool AreTerminal => _requests.All(request => request.IsCompleted);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        private async Task<bool> WaitForTerminalAsync()
        {
            var all = Task.WhenAll(_requests);
            var completed = await Task.WhenAny(all, Task.Delay(TerminalTimeout));
            return ReferenceEquals(completed, all);
        }

        private string DescribeRequests() => string.Join("; ", _requests.Select((request, index) => $"{index}:{request.Status}:{request.Exception}"));
        public string DescribeForDiagnostics() => DescribeRequests();

        private static void ThrowCleanupFailures(List<Exception> failures)
        {
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    private static class DeferredRaceOwnership
    {
        private static readonly ConcurrentDictionary<Guid, DeferredRaceEntry> Retained = new();
        private static readonly ConcurrentQueue<Exception> Diagnostics = new();

        public static int Count => Retained.Count;
        public static IReadOnlyList<Exception> DiagnosticSnapshot => Diagnostics.ToArray();

        public static Guid Retain(PreferenceWriteGate gate, RaceWriteBatch batch)
        {
            var id = Guid.NewGuid();
            var entry = new DeferredRaceEntry(id, gate, batch);
            if (!Retained.TryAdd(id, entry)) throw new InvalidOperationException("Failed to retain deferred race ownership.");
            entry.Start();
            return id;
        }

        public static async Task<DeferredRaceDrainReport> DrainAsync(CancellationToken cancellationToken)
        {
            var entries = Retained.Values.ToArray();
            var failures = new List<Exception>();
            foreach (var entry in entries)
            {
                try
                {
                    var report = await entry.Completion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
                    failures.AddRange(report.Failures);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    Diagnostics.Enqueue(exception);
                }
            }

            return new(entries.Length, Retained.Count, failures.ToArray());
        }

        private sealed class DeferredRaceEntry(
            Guid id,
            PreferenceWriteGate gate,
            RaceWriteBatch batch)
        {
            public TaskCompletionSource<DeferredRaceDrainEntryReport> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Start() => _ = DrainEntryAsync();

            private async Task DrainEntryAsync()
            {
                var failures = new List<Exception>();
                var terminal = false;
                try
                {
                    for (var attempt = 0; attempt < 3 && !terminal; attempt++)
                    {
                        try
                        {
                            terminal = await batch.EnsureTerminalAsync(gate);
                        }
                        catch (Exception exception)
                        {
                            failures.Add(exception);
                            terminal = batch.AreTerminal;
                        }

                        if (!terminal)
                            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
                    }

                    if (!terminal)
                        failures.Add(new TimeoutException($"Deferred race ownership {id} remains unresolved: {batch.DescribeForDiagnostics()}"));

                    if (terminal)
                    {
                        try { await gate.DisposeAsync(); }
                        catch (Exception exception) { failures.Add(exception); }
                        try { batch.Dispose(); }
                        catch (Exception exception) { failures.Add(exception); }
                        Retained.TryRemove(id, out _);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    Diagnostics.Enqueue(exception);
                }

                foreach (var failure in failures) Diagnostics.Enqueue(failure);
                Completion.TrySetResult(new(terminal, failures.ToArray()));
            }
        }
    }

    private sealed record DeferredRaceDrainReport(
        int ObservedEntries,
        int RemainingEntries,
        IReadOnlyList<Exception> Failures);

    private sealed record DeferredRaceDrainEntryReport(
        bool Terminal,
        IReadOnlyList<Exception> Failures);

    private sealed record RacePutResponse(
        HttpStatusCode StatusCode,
        string ETag,
        TimezonePreferenceResponse? Body,
        string? ProblemCode,
        string? CurrentEtag,
        string? CorrelationId,
        string? ProblemBody = null,
        string? ContentType = null);

    private sealed record TimezonePreferenceSnapshot(
        string Issuer,
        string Subject,
        string? Timezone,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
    private sealed record AuditEventSnapshot(
        Guid Id,
        Guid? ActorId,
        string Action,
        string EntityType,
        Guid? EntityId,
        string? BeforeJson,
        string? AfterJson,
        string CorrelationId,
        DateTimeOffset OccurredAt,
        string? SourceIp)
    {
        public IEnumerable<string> StringScalars()
        {
            yield return Action;
            yield return EntityType;
            yield return BeforeJson ?? string.Empty;
            yield return AfterJson ?? string.Empty;
            yield return CorrelationId;
            yield return SourceIp ?? string.Empty;
            yield return EntityId?.ToString("D") ?? string.Empty;
            yield return ActorId?.ToString("D") ?? string.Empty;
        }
    }
    private sealed record PolicyMatrix(string Name, IReadOnlyList<string> AllowedRoles);
    private sealed record RoleCombination(string Policy, IReadOnlyList<string> Roles, bool Allowed);
}
