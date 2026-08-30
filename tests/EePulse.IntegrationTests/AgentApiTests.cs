using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Inventory;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using EePulse.Api.Agents;
using EePulse.Application.Time;
using EePulse.Domain.Agents;
using EePulse.Domain.Status;
using EePulse.Domain.Inventory;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Npgsql;

namespace EePulse.IntegrationTests;

public sealed class AgentApiTests
{
    private static readonly Guid ActorId = Guid.Parse("909e92f8-bad6-4bbd-b3f2-57000dc30c31");
    private static readonly System.Text.Json.JsonSerializerOptions AgentJson = CreateAgentJson();

    [Fact]
    public async Task EnrollmentRequiresPublishedPolicyAndExpiredOrRevokedTokensNeverMutate()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient(); var group = await CreateGroup(client, ct);
        using (var noPolicy = AdminRequest(HttpMethod.Post, "/api/v1/agent-enrollment-tokens", AgentContent(new CreateAgentEnrollmentTokenRequest(1, Guid.Parse(group.Id), "no-policy", null, ["192.0.2.0/24"])))) Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(noPolicy, ct)).StatusCode);
        _ = await SendAdmin<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/25"], group.RowVersion), ct);
        var expired = await Issue(client, group.Id, ct); await using (var scope = factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_enrollment_tokens SET expires_at = {DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE id = {expired.TokenId}", ct); }
        Assert.Equal(HttpStatusCode.Gone, (await PostAgentJson(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, expired.EnrollmentToken, Guid.NewGuid(), "expired", "1.2.3", expired.AllowedNetworks, DateTimeOffset.UtcNow), ct)).StatusCode);
        var revoked = await Issue(client, group.Id, ct); using (var revoke = AdminRequest(HttpMethod.Delete, $"/api/v1/agent-enrollment-tokens/{revoked.TokenId}", null)) Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(revoke, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await PostAgentJson(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, revoked.EnrollmentToken, Guid.NewGuid(), "revoked", "1.2.3", revoked.AllowedNetworks, DateTimeOffset.UtcNow), ct)).StatusCode);
        var bound = await SendAdmin<CreateAgentEnrollmentTokenResponse>(client, HttpMethod.Post, "/api/v1/agent-enrollment-tokens", new CreateAgentEnrollmentTokenRequest(1, Guid.Parse(group.Id), "machine-bound", "approved-machine", ["192.0.2.0/24"]), ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAgentJson(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, bound.EnrollmentToken, Guid.NewGuid(), "wrong-machine", "1.2.3", bound.AllowedNetworks, DateTimeOffset.UtcNow), ct)).StatusCode);
        var boundEnrolled = await Enroll(client, bound, Guid.NewGuid(), "approved-machine", ct);
        await using (var expireScope = factory.Services.CreateAsyncScope()) { var db = expireScope.ServiceProvider.GetRequiredService<EePulseDbContext>(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_credentials SET expires_at = {DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE id = {boundEnrolled.CredentialId}", ct); }
        using (var expiredCredential = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{boundEnrolled.AgentId}/heartbeat", boundEnrolled.AgentCredential, AgentContent(Heartbeat(Guid.NewGuid())))) Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(expiredCredential, ct)).StatusCode);
        await using var verify = factory.Services.CreateAsyncScope(); Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<EePulseDbContext>().Agents.CountAsync(ct));
    }

    [Fact]
    public async Task MissingAndMalformedAgentCredentialsReturnSanitized401WithoutMutation()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct); await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient(); var agentId = Guid.NewGuid();
        foreach (var credential in new string?[] { null, "malformed-secret-canary" })
        { using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{agentId}/heartbeat") { Content = AgentContent(Heartbeat(Guid.NewGuid())) }; if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential); var response = await client.SendAsync(request, ct); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); var body = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain("malformed-secret-canary", body, StringComparison.Ordinal); Assert.Equal(AgentProblemCodes.AgentAuthenticationRequired, System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()); }
        await using var scope = factory.Services.CreateAsyncScope(); Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().Agents.CountAsync(ct));
    }

    [Fact]
    public async Task ProbeAndDeviceWireMutationsPublishAtomicallyAndAuditTheActor()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct); await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient(); var group = await CreateGroup(client, ct); _ = await SendAdmin<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/24"], group.RowVersion), ct);
        var site = await SendAdmin<SiteResponse>(client, HttpMethod.Post, "/api/v1/sites", new CreateSiteRequest("PUB", "Publication", "UTC"), ct); var device = await SendAdmin<DeviceResponse>(client, HttpMethod.Post, "/api/v1/devices", new CreateDeviceRequest(site.Id, "target", "192.0.2.10", null, "server", null, null, "Normal", []), ct);
        _ = await SendAdmin<ProbeResponse>(client, HttpMethod.Post, "/api/v1/probes", new CreateProbeRequest(device.Id, group.Id, 20, 1000, 1, null, null, 1, 1), ct);
        long snapshotCount; await using (var scope = factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>(); snapshotCount = await db.AgentConfigurationSnapshots.CountAsync(x => x.AgentGroupId == Guid.Parse(group.Id), ct); Assert.Equal(2, snapshotCount); var latest = await db.AgentConfigurationSnapshots.OrderByDescending(x => x.Version).FirstAsync(x => x.AgentGroupId == Guid.Parse(group.Id), ct); Assert.Equal(2, latest.Version); Assert.Equal(32, latest.PayloadDigest.Length); using var payload = System.Text.Json.JsonDocument.Parse(latest.Payload); var probes = payload.RootElement.GetProperty("probes"); Assert.Equal(1, probes.GetArrayLength()); var configuredProbe = probes[0]; Assert.Equal("icmp", configuredProbe.GetProperty("type").GetString()); Assert.Equal("192.0.2.10", configuredProbe.GetProperty("targetAddress").GetString()); Assert.Equal(1, configuredProbe.GetProperty("failureThreshold").GetInt32()); var publication = await db.AuditEvents.OrderByDescending(x => x.OccurredAt).FirstAsync(x => x.Action == "agent.configuration.published" && x.EntityId == Guid.Parse(group.Id), ct); Assert.Equal(ActorId, publication.ActorId); Assert.NotEqual("configuration-publication", publication.CorrelationId); }
        using var invalid = AdminRequest(HttpMethod.Put, $"/api/v1/devices/{device.Id}", AgentContent(new UpdateDeviceRequest(site.Id, device.Name, "203.0.113.10", device.Hostname, device.DeviceType, device.Area, device.Owner, device.Criticality, device.Tags, true, device.RowVersion))); var response = await client.SendAsync(invalid, ct); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct); Assert.Equal(AgentProblemCodes.ConfigurationConflict, problem.GetProperty("code").GetString()); Assert.False(problem.GetProperty("retryable").GetBoolean());
        await using var verify = factory.Services.CreateAsyncScope(); var verifyDb = verify.ServiceProvider.GetRequiredService<EePulseDbContext>(); Assert.Equal("192.0.2.10", (await verifyDb.Devices.SingleAsync(x => x.Id == Guid.Parse(device.Id), ct)).Address); Assert.Equal(snapshotCount, await verifyDb.AgentConfigurationSnapshots.CountAsync(x => x.AgentGroupId == Guid.Parse(group.Id), ct));
    }

    [Fact]
    public async Task EnrollmentBodyAndSourceRateLimitsReturnSanitizedProblems()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient();
        var canary = $"secret-canary-{Guid.NewGuid():N}"; var bytes = System.Text.Encoding.UTF8.GetBytes($"{{\"enrollmentToken\":\"{canary}{new string('x', 33_000)}\"}}");
        using (var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/enroll") { Content = new UnknownLengthContent(bytes) })
        { oversized.Content.Headers.ContentType = new("application/json"); var response = await client.SendAsync(oversized, ct); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); Assert.DoesNotContain(canary, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal); }
        const string nonZulu = "2026-08-11T00:00:00+00:00"; using (var timestampRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/enroll") { Content = new StringContent($"{{\"schemaVersion\":1,\"enrollmentToken\":\"00000000000000000000000000000000.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"clientInstanceId\":\"11111111-1111-1111-1111-111111111111\",\"machineName\":\"probe\",\"agentVersion\":\"1.2.3\",\"localAllowedNetworks\":[\"192.0.2.0/24\"],\"sentAt\":\"{nonZulu}\"}}", System.Text.Encoding.UTF8, "application/json") })
        { var response = await client.SendAsync(timestampRequest, ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); var text = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain(nonZulu, text, StringComparison.Ordinal); var timestampProblem = System.Text.Json.JsonDocument.Parse(text).RootElement; Assert.Equal(AgentProblemCodes.TimestampNotUtc, timestampProblem.GetProperty("code").GetString()); Assert.False(timestampProblem.GetProperty("retryable").GetBoolean()); Assert.False(string.IsNullOrWhiteSpace(timestampProblem.GetProperty("correlationId").GetString())); }
        foreach (var malformedBody in new[] { "{", "{\"schemaVersion\":1,\"sentAt\":\"not-a-dateZ\"}" })
        {
            using var malformedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/enroll") { Content = new StringContent(malformedBody, System.Text.Encoding.UTF8, "application/json") };
            var response = await client.SendAsync(malformedRequest, ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain("not-a-dateZ", body, StringComparison.Ordinal); var value = System.Text.Json.JsonDocument.Parse(body).RootElement;
            Assert.Equal(malformedBody == "{" ? AgentProblemCodes.RequestInvalid : AgentProblemCodes.TimestampNotUtc, value.GetProperty("code").GetString()); Assert.False(value.GetProperty("retryable").GetBoolean()); Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("correlationId").GetString()));
        }
        const string prohibited = "{\"schemaVersion\":1,\"command\":\"secret-canary-command\",\"url\":\"https://example.invalid\"}";
        using (var prohibitedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/enroll") { Content = new StringContent(prohibited, System.Text.Encoding.UTF8, "application/json") })
        { var response = await client.SendAsync(prohibitedRequest, ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); var body = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain("secret-canary-command", body, StringComparison.Ordinal); Assert.Equal(AgentProblemCodes.RequestInvalid, System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()); }
        var sourceResponses = new List<HttpResponseMessage>(); for (var attempt = 0; attempt < 6; attempt++) sourceResponses.Add(await PostAgentJson(client, "/api/v1/agents/enroll", new { schemaVersion = 1 }, ct)); Assert.All(sourceResponses.Take(5), response => Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode)); var limited = sourceResponses[5];
        Assert.NotNull(limited); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var problem = await limited.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct); Assert.Equal(AgentProblemCodes.RateLimitExceeded, problem.GetProperty("code").GetString());
        await using var tokenFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => { builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString); builder.ConfigureTestServices(services => services.AddSingleton(new AgentRateLimitDefaults(100, 20, 60))); }); using var tokenClient = tokenFactory.CreateClient(); var tokenWire = $"{Guid.NewGuid():N}.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; var tokenResponses = new List<HttpResponseMessage>(); for (var attempt = 0; attempt < 21; attempt++) tokenResponses.Add(await PostAgentJson(tokenClient, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, tokenWire, Guid.NewGuid(), "rate-probe", "1.2.3", ["192.0.2.0/24"], DateTimeOffset.UtcNow), ct)); Assert.All(tokenResponses.Take(20), response => Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode)); Assert.Equal(HttpStatusCode.TooManyRequests, tokenResponses[20].StatusCode); Assert.True(tokenResponses[20].Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task EnrollmentHeartbeatConfigurationRotationRevocationAndAuthenticationSeparationWork()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        using var client = factory.CreateClient();
        var group = await CreateGroup(client, ct);
        var policy = await SendAdmin<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["198.51.100.0/24", "192.0.2.0/24"], group.RowVersion), ct);
        Assert.Equal(1, policy.ConfigurationVersion);
        var issued = await Issue(client, group.Id, ct);

        var malformedTokenResponse = await PostAgentJson(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, "malformed", Guid.NewGuid(), "probe-a", "1.2.3", issued.AllowedNetworks, DateTimeOffset.UtcNow), ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformedTokenResponse.StatusCode);
        var unknownToken = $"{Guid.NewGuid():N}.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var unknownTokenResponse = await PostAgentJson(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, unknownToken, Guid.NewGuid(), "probe-a", "1.2.3", issued.AllowedNetworks, DateTimeOffset.UtcNow), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownTokenResponse.StatusCode);

        var mismatch = new AgentEnrollmentRequest(1, issued.EnrollmentToken, Guid.NewGuid(), "probe-a", "1.2.3", ["203.0.113.0/24"], DateTimeOffset.UtcNow);
        var mismatchResponse = await PostAgentJson(client, "/api/v1/agents/enroll", mismatch, ct);
        Assert.Equal(HttpStatusCode.Forbidden, mismatchResponse.StatusCode);

        var enrolled = await Enroll(client, issued, Guid.NewGuid(), "probe-a", ct);
        var replay = await PostAgentJson(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, issued.EnrollmentToken, Guid.NewGuid(), "probe-b", "1.2.3", issued.AllowedNetworks, DateTimeOffset.UtcNow), ct);
        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);

        using (var userHeartbeat = AdminRequest(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/heartbeat", AgentContent(Heartbeat(Guid.NewGuid()))))
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(userHeartbeat, ct)).StatusCode);
        using (var agentInventory = AgentRequest(HttpMethod.Get, "/api/v1/devices", enrolled.AgentCredential))
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(agentInventory, ct)).StatusCode);
        using (var wrongRoute = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{Guid.NewGuid()}/heartbeat", enrolled.AgentCredential, AgentContent(Heartbeat(Guid.NewGuid())))) Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongRoute, ct)).StatusCode);
        await using (var routeScope = factory.Services.CreateAsyncScope()) Assert.Equal(0, await routeScope.ServiceProvider.GetRequiredService<EePulseDbContext>().AgentHeartbeatReceipts.CountAsync(ct));

        var heartbeatId = Guid.NewGuid();
        var heartbeat = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/heartbeat", enrolled.AgentCredential, Heartbeat(heartbeatId), HttpStatusCode.OK, ct);
        var duplicate = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/heartbeat", enrolled.AgentCredential, Heartbeat(heartbeatId), HttpStatusCode.OK, ct);
        Assert.Equal(heartbeat, duplicate);
        var skewed = Heartbeat(Guid.NewGuid()) with { SentAt = DateTimeOffset.UtcNow.AddMinutes(-10) }; var skewResponse = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/heartbeat", enrolled.AgentCredential, skewed, HttpStatusCode.OK, ct); Assert.True(skewResponse.ClockSkewSuspected);

        using var configurationRequest = AgentRequest(HttpMethod.Get, $"/api/v1/agents/{enrolled.AgentId}/configuration", enrolled.AgentCredential);
        var configurationResponse = await client.SendAsync(configurationRequest, ct);
        Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
        Assert.NotNull(configurationResponse.Headers.ETag);
        var configuration = await configurationResponse.Content.ReadFromJsonAsync<AgentConfigurationResponse>(ct);
        Assert.NotNull(configuration);
        var configurationJson = await configurationResponse.Content.ReadAsStringAsync(ct); Assert.Contains("Z\"", configurationJson, StringComparison.Ordinal); Assert.DoesNotContain("+00:00", configurationJson, StringComparison.Ordinal);
        using (var repeatedRequest = AgentRequest(HttpMethod.Get, $"/api/v1/agents/{enrolled.AgentId}/configuration", enrolled.AgentCredential)) { var repeated = await client.SendAsync(repeatedRequest, ct); Assert.Equal(configurationResponse.Headers.ETag, repeated.Headers.ETag); Assert.Equal(configurationJson, await repeated.Content.ReadAsStringAsync(ct)); }
        using var conditional = AgentRequest(HttpMethod.Get, $"/api/v1/agents/{enrolled.AgentId}/configuration", enrolled.AgentCredential);
        conditional.Headers.IfNoneMatch.Add(configurationResponse.Headers.ETag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional, ct)).StatusCode);

        var acknowledgementId = Guid.NewGuid();
        using (var futureAck = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/configuration/acknowledgements", enrolled.AgentCredential, AgentContent(new AgentConfigurationAcknowledgementRequest(1, Guid.NewGuid(), configuration.ConfigurationVersion + 1, "Applied", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow)))) Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(futureAck, ct)).StatusCode);
        var acknowledgement = new AgentConfigurationAcknowledgementRequest(1, acknowledgementId, configuration.ConfigurationVersion, "Applied", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow);
        var concurrentAcknowledgements = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAgent<AgentConfigurationAcknowledgementResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/configuration/acknowledgements", enrolled.AgentCredential, acknowledgement, HttpStatusCode.OK, ct)));
        var accepted = concurrentAcknowledgements[0]; Assert.Equal(accepted, concurrentAcknowledgements[1]);
        Assert.Equal(configuration.ConfigurationVersion, accepted.CentralEffectiveConfigurationVersion);
        long groupRowVersion; await using (var groupScope = factory.Services.CreateAsyncScope()) groupRowVersion = (await groupScope.ServiceProvider.GetRequiredService<EePulseDbContext>().AgentGroups.AsNoTracking().SingleAsync(x => x.Id == enrolled.AgentGroupId, ct)).RowVersion; var rollbackBody = new RollbackAgentConfigurationRequest(1, configuration.ConfigurationVersion, groupRowVersion); var rollbackRequests = Enumerable.Range(0, 2).Select(_ => AdminRequest(HttpMethod.Post, $"/api/v1/agent-groups/{enrolled.AgentGroupId}/configuration/rollback", AgentContent(rollbackBody))).ToArray(); var rollbackResponses = await Task.WhenAll(rollbackRequests.Select(request => client.SendAsync(request, ct))); Assert.Single(rollbackResponses, x => x.StatusCode == HttpStatusCode.Created); Assert.Single(rollbackResponses, x => x.StatusCode == HttpStatusCode.Conflict); var rollback = (await rollbackResponses.Single(x => x.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<AgentConfigurationPublicationResponse>(ct))!; Assert.Equal(configuration.ConfigurationVersion + 1, rollback.ConfigurationVersion); Assert.Equal(configuration.ConfigurationVersion, rollback.RollbackOfVersion);
        AgentConfigurationResponse rolledConfiguration; using (var rolledRequest = AgentRequest(HttpMethod.Get, $"/api/v1/agents/{enrolled.AgentId}/configuration", enrolled.AgentCredential)) { var rolledResponse = await client.SendAsync(rolledRequest, ct); rolledConfiguration = (await rolledResponse.Content.ReadFromJsonAsync<AgentConfigurationResponse>(ct))!; Assert.Equal(rollback.ConfigurationVersion, rolledConfiguration.ConfigurationVersion); Assert.Equal(configuration.Probes, rolledConfiguration.Probes); }
        _ = await SendAgent<AgentConfigurationAcknowledgementResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/configuration/acknowledgements", enrolled.AgentCredential, new AgentConfigurationAcknowledgementRequest(1, Guid.NewGuid(), rolledConfiguration.ConfigurationVersion, "Applied", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow), HttpStatusCode.OK, ct);
        using (var staleAck = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/configuration/acknowledgements", enrolled.AgentCredential, AgentContent(new AgentConfigurationAcknowledgementRequest(1, Guid.NewGuid(), configuration.ConfigurationVersion, "Applied", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow)))) Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(staleAck, ct)).StatusCode);

        using (var unknownRotation = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/credentials/rotate", enrolled.AgentCredential, new StringContent("{\"schemaVersion\":1,\"unexpectedField\":\"secret-canary-command\"}", System.Text.Encoding.UTF8, "application/json")))
        { var response = await client.SendAsync(unknownRotation, ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); var body = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain("secret-canary-command", body, StringComparison.Ordinal); Assert.Equal(AgentProblemCodes.RequestInvalid, System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()); }
        using (var invalidType = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/credentials/rotate", enrolled.AgentCredential, new StringContent("{\"schemaVersion\":\"secret-canary-invalid-type\"}", System.Text.Encoding.UTF8, "application/json")))
        { var response = await client.SendAsync(invalidType, ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); var body = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain("secret-canary-invalid-type", body, StringComparison.Ordinal); Assert.Equal(AgentProblemCodes.RequestInvalid, System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()); }

        var rotateRequests = Enumerable.Range(0, 2).Select(_ => AgentRequest(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/credentials/rotate", enrolled.AgentCredential, AgentContent(new RotateAgentCredentialRequest(1)))).ToArray();
        var rotateResponses = await Task.WhenAll(rotateRequests.Select(request => client.SendAsync(request, ct))); Assert.All(rotateResponses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var issuedRotations = await Task.WhenAll(rotateResponses.Select(response => response.Content.ReadFromJsonAsync<RotateAgentCredentialResponse>(ct)));
        Guid survivingCredentialId; await using (var rotationScope = factory.Services.CreateAsyncScope()) { var pendingCredentials = await rotationScope.ServiceProvider.GetRequiredService<EePulseDbContext>().AgentCredentials.Where(x => x.AgentId == enrolled.AgentId && x.State == EePulse.Domain.Agents.AgentCredentialState.Pending).ToListAsync(ct); Assert.Single(pendingCredentials); survivingCredentialId = pendingCredentials[0].Id; }
        var rotated = issuedRotations.Single(x => x!.CredentialId == survivingCredentialId)!; var superseded = issuedRotations.Single(x => x!.CredentialId != survivingCredentialId)!;
        using (var supersededRequest = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/heartbeat", superseded.AgentCredential, AgentContent(Heartbeat(Guid.NewGuid())))) Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(supersededRequest, ct)).StatusCode);
        var promotionUses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/heartbeat", rotated.AgentCredential, Heartbeat(Guid.NewGuid()), HttpStatusCode.OK, ct)));
        Assert.Equal(2, promotionUses.Length);
        using var oldCredential = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/heartbeat", enrolled.AgentCredential, AgentContent(Heartbeat(Guid.NewGuid())));
        var oldResponse = await client.SendAsync(oldCredential, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);
        var oldProblem = await oldResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        Assert.Equal(AgentProblemCodes.AgentAuthenticationRequired, oldProblem.GetProperty("code").GetString());

        await using (var tamperScope = factory.Services.CreateAsyncScope())
        {
            var tamperDb = tamperScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            await tamperDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_configuration_snapshots SET payload = jsonb_set(payload, '{{schemaVersion}}', '99'::jsonb) WHERE agent_group_id = {enrolled.AgentGroupId} AND version = {rolledConfiguration.ConfigurationVersion}", ct);
        }
        using (var tampered = AgentRequest(HttpMethod.Get, $"/api/v1/agents/{enrolled.AgentId}/configuration", rotated.AgentCredential))
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(tampered, ct)).StatusCode);

        var details = await SendAdmin<AgentResponse>(client, HttpMethod.Get, $"/api/v1/agents/{enrolled.AgentId}", null, ct);
        var revoked = await SendAdmin<AgentResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/revoke", new RevokeAgentRequest(1, "Administrative", details.RowVersion), ct);
        Assert.Equal("Revoked", revoked.Status);
        using var afterRevocation = AgentRequest(HttpMethod.Get, $"/api/v1/agents/{enrolled.AgentId}/configuration", rotated.AgentCredential);
        Assert.Equal(HttpStatusCode.Gone, (await client.SendAsync(afterRevocation, ct)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Equal(32, (await db.AgentEnrollmentTokens.SingleAsync(x => x.Id == issued.TokenId, ct)).Digest.Length);
        Assert.All(await db.AgentCredentials.Where(x => x.AgentId == enrolled.AgentId).ToListAsync(ct), credential => Assert.Equal(32, credential.Digest.Length));
        var auditJson = string.Join('\n', (await db.AuditEvents.ToListAsync(ct)).Select(x => x.AfterJson));
        Assert.DoesNotContain(issued.EnrollmentToken, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(enrolled.AgentCredential, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(rotated.AgentCredential, auditJson, StringComparison.Ordinal);
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.Action == "agent.credential.promoted" && x.EntityId == enrolled.AgentId, ct));
    }

    [Fact]
    public async Task TenConcurrentEnrollmentAttemptsHaveExactlyOneWinner()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => { builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString); builder.ConfigureTestServices(services => services.AddSingleton(new AgentRateLimitDefaults(100, 20, 60))); }); using var client = factory.CreateClient(); var group = await CreateGroup(client, ct); _ = await SendAdmin<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/24"], group.RowVersion), ct); var issued = await Issue(client, group.Id, ct);
        var requests = Enumerable.Range(0, 10).Select(index => PostAgentJson(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, issued.EnrollmentToken, Guid.NewGuid(), $"probe-{index}", "1.2.3", issued.AllowedNetworks, DateTimeOffset.UtcNow), ct)).ToArray();
        var responses = await Task.WhenAll(requests); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created); Assert.Equal(9, responses.Count(x => x.StatusCode == HttpStatusCode.Gone));
        await using var scope = factory.Services.CreateAsyncScope(); Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().Agents.CountAsync(ct));
    }

    [Fact]
    public async Task ConfigurationPublicationMaterializesFrozenPolicyLineageAndAppliedAcknowledgementsCreateStableBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        var applicationNow = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUtcClock>();
                services.AddSingleton<IUtcClock>(new FixedClock(applicationNow));
            });
        });
        using var client = factory.CreateClient();
        var group = await CreateGroup(client, ct);
        _ = await SendAdmin<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/24"], group.RowVersion), ct);
        var site = await SendAdmin<SiteResponse>(client, HttpMethod.Post, "/api/v1/sites", new CreateSiteRequest("LIN", "Lineage", "UTC"), ct);
        var device = await SendAdmin<DeviceResponse>(client, HttpMethod.Post, "/api/v1/devices", new CreateDeviceRequest(site.Id, "lineage", "192.0.2.80", null, "server", null, null, "Normal", []), ct);
        var firstProbe = await SendAdmin<ProbeResponse>(client, HttpMethod.Post, "/api/v1/probes", new CreateProbeRequest(device.Id, group.Id, 30, 2_000, 3, 500, null, 1, 1), ct);
        var secondProbe = await SendAdmin<ProbeResponse>(client, HttpMethod.Post, "/api/v1/probes", new CreateProbeRequest(device.Id, group.Id, 30, 2_000, 3, 500, null, 1, 1), ct);
        var groupId = Guid.Parse(group.Id);
        var firstProbeId = Guid.Parse(firstProbe.Id);
        var secondProbeId = Guid.Parse(secondProbe.Id);

        Guid firstPolicyId;
        await using (var firstScope = factory.Services.CreateAsyncScope())
        {
            var db = firstScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var bindings = await db.ProbeStatusPolicyBindings.AsNoTracking().Where(x => x.AgentGroupId == groupId && x.ConfigurationVersion == 3).OrderBy(x => x.ProbeId).ToListAsync(ct);
            Assert.Equal(2, bindings.Count);
            Assert.All(bindings, binding => Assert.Contains(binding.ProbeId, new[] { firstProbeId, secondProbeId }));
            Assert.Single(bindings.Select(x => x.PolicySnapshotId).Distinct());
            firstPolicyId = bindings[0].PolicySnapshotId;
            Assert.Single(await db.ProbeStatusPolicySnapshots.AsNoTracking().ToListAsync(ct));
            var firstPolicy = await db.ProbeStatusPolicySnapshots.AsNoTracking().SingleAsync(x => x.Id == firstPolicyId, ct);
            Assert.Equal(1, firstPolicy.PolicyVersion);
            Assert.Equal(1, firstPolicy.FailureThreshold);
            Assert.Equal(1, firstPolicy.RecoveryThreshold);
            Assert.Equal(500, firstPolicy.WarningRttMilliseconds);
            Assert.Equal(0.05m, firstPolicy.WarningPacketLossRatio);
            Assert.Equal(300, firstPolicy.ApprovedLatenessSeconds);
            Assert.Equal(60, firstPolicy.ApprovedFutureSkewSeconds);
            foreach (var binding in bindings)
                Assert.True(await db.AgentConfigurationSnapshots.AsNoTracking().AnyAsync(snapshot => snapshot.AgentGroupId == binding.AgentGroupId && snapshot.Version == binding.ConfigurationVersion, ct));
        }

        var issued = await Issue(client, group.Id, ct);
        var enrolled = await Enroll(client, issued, Guid.NewGuid(), "lineage-agent", ct);
        var unchanged = await SendAdmin<AgentNetworkPolicyResponse>(
    client,
    HttpMethod.Put,
    $"/api/v1/agents/{enrolled.AgentId}/allowed-networks",
    new UpdateAgentAllowedNetworksRequest(
        1,
        ["192.0.2.0/24"],
        1),
    ct);
        Assert.Equal(4, unchanged.ConfigurationVersion);
        await using (var unchangedScope = factory.Services.CreateAsyncScope())
        {
            var db = unchangedScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var bindings = await db.ProbeStatusPolicyBindings.AsNoTracking().Where(x => x.AgentGroupId == groupId && x.ConfigurationVersion == unchanged.ConfigurationVersion).ToListAsync(ct);
            Assert.Equal(2, bindings.Count);
            Assert.All(bindings, binding => Assert.Equal(firstPolicyId, binding.PolicySnapshotId));
        }

        var failureChanged = await SendAdmin<ProbeResponse>(client, HttpMethod.Put, $"/api/v1/probes/{firstProbe.Id}", new UpdateProbeRequest(group.Id, 30, 2_000, 3, 500, null, 4, 1, true, firstProbe.RowVersion), ct);
        Guid failurePolicyId;
        await using (var failureChangedScope = factory.Services.CreateAsyncScope())
        {
            var db = failureChangedScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var bindings = await db.ProbeStatusPolicyBindings.AsNoTracking().Where(x => x.AgentGroupId == groupId && x.ConfigurationVersion == 5).ToDictionaryAsync(x => x.ProbeId, ct);
            failurePolicyId = bindings[firstProbeId].PolicySnapshotId;
            Assert.NotEqual(firstPolicyId, failurePolicyId);
            Assert.Equal(firstPolicyId, bindings[secondProbeId].PolicySnapshotId);
            var failurePolicy = await db.ProbeStatusPolicySnapshots.AsNoTracking().SingleAsync(x => x.Id == failurePolicyId, ct);
            Assert.Equal(4, failurePolicy.FailureThreshold);
            Assert.Equal(1, failurePolicy.RecoveryThreshold);
            Assert.Equal(500, failurePolicy.WarningRttMilliseconds);
        }

        _ = await SendAdmin<ProbeResponse>(client, HttpMethod.Put, $"/api/v1/probes/{firstProbe.Id}", new UpdateProbeRequest(group.Id, 30, 2_000, 3, 500, null, 4, 2, true, failureChanged.RowVersion), ct);
        await using (var recoveryChangedScope = factory.Services.CreateAsyncScope())
        {
            var db = recoveryChangedScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var bindings = await db.ProbeStatusPolicyBindings.AsNoTracking().Where(x => x.AgentGroupId == groupId && x.ConfigurationVersion == 6).ToDictionaryAsync(x => x.ProbeId, ct);
            var recoveryPolicyId = bindings[firstProbeId].PolicySnapshotId;
            Assert.NotEqual(firstPolicyId, recoveryPolicyId);
            Assert.NotEqual(failurePolicyId, recoveryPolicyId);
            Assert.Equal(firstPolicyId, bindings[secondProbeId].PolicySnapshotId);
            var recoveryPolicy = await db.ProbeStatusPolicySnapshots.AsNoTracking().SingleAsync(x => x.Id == recoveryPolicyId, ct);
            Assert.Equal(4, recoveryPolicy.FailureThreshold);
            Assert.Equal(2, recoveryPolicy.RecoveryThreshold);
            Assert.Equal(500, recoveryPolicy.WarningRttMilliseconds);
        }

        long groupRowVersion;
        await using (var rollbackScope = factory.Services.CreateAsyncScope()) groupRowVersion = await rollbackScope.ServiceProvider.GetRequiredService<EePulseDbContext>().AgentGroups.AsNoTracking().Where(x => x.Id == groupId).Select(x => x.RowVersion).SingleAsync(ct);
        var rollback = await SendAdmin<AgentConfigurationPublicationResponse>(client, HttpMethod.Post, $"/api/v1/agent-groups/{group.Id}/configuration/rollback", new RollbackAgentConfigurationRequest(1, 3, groupRowVersion), ct);
        await using (var rollbackScope = factory.Services.CreateAsyncScope())
        {
            var db = rollbackScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var bindings = await db.ProbeStatusPolicyBindings.AsNoTracking().Where(x => x.AgentGroupId == groupId && x.ConfigurationVersion == rollback.ConfigurationVersion).ToDictionaryAsync(x => x.ProbeId, ct);
            Assert.Equal(firstPolicyId, bindings[firstProbeId].PolicySnapshotId);
            Assert.Equal(firstPolicyId, bindings[secondProbeId].PolicySnapshotId);
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_policy_bindings SET policy_snapshot_id = {Guid.NewGuid()} WHERE probe_id = {firstProbeId} AND configuration_version = {rollback.ConfigurationVersion}", ct));
        }

        await using (var beforeAppliedScope = factory.Services.CreateAsyncScope())
        {
            var db = beforeAppliedScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            Assert.True(await db.ProbeStatusPolicyBindings.AnyAsync(x => x.ProbeId == firstProbeId && x.ConfigurationVersion == rollback.ConfigurationVersion, ct));
            Assert.False(await db.AgentConfigurationEffectiveBoundaries.AnyAsync(x => x.AgentId == enrolled.AgentId && x.ConfigurationVersion == rollback.ConfigurationVersion, ct));
            var unresolvedResultId = Guid.NewGuid();
            var unresolvedAt = DateTimeOffset.UtcNow;
            db.Add(new ProbeResultLedgerEntry(enrolled.AgentId, unresolvedResultId, firstProbeId, rollback.ConfigurationVersion, unresolvedAt.AddSeconds(-1), unresolvedAt, 3, 3, 0m, 1m, 1m, 1m, null, new byte[32], unresolvedAt));
            await db.SaveChangesAsync(ct);
            var unresolved = await new ProbeResultStatusProcessor(db, new FixedClock(unresolvedAt)).ProcessNextAsync(firstProbeId, ct);
            Assert.Equal(ProbeResultProcessingDispositionKind.HistoricalOther, unresolved.Disposition);
            Assert.Empty(await db.ProbeStatusProjections.AsNoTracking().ToListAsync(ct));
        }

        var rejectedId = Guid.NewGuid();
        _ = await SendAgent<AgentConfigurationAcknowledgementResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/configuration/acknowledgements", enrolled.AgentCredential, new AgentConfigurationAcknowledgementRequest(1, rejectedId, rollback.ConfigurationVersion, "Rejected", null, AgentConfigurationRejectionCodes.ConfigurationInvalid, DateTimeOffset.UtcNow), HttpStatusCode.OK, ct);
        await using (var rejectedScope = factory.Services.CreateAsyncScope()) Assert.False(await rejectedScope.ServiceProvider.GetRequiredService<EePulseDbContext>().AgentConfigurationEffectiveBoundaries.AnyAsync(x => x.AgentId == enrolled.AgentId && x.ConfigurationVersion == rollback.ConfigurationVersion, ct));

        var appliedId = Guid.NewGuid();
        var agentAppliedAt = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var appliedRequest = new AgentConfigurationAcknowledgementRequest(1, appliedId, rollback.ConfigurationVersion, "Applied", agentAppliedAt, null, DateTimeOffset.UtcNow);
        var appliedResponses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAgent<AgentConfigurationAcknowledgementResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/configuration/acknowledgements", enrolled.AgentCredential, appliedRequest, HttpStatusCode.OK, ct)));
        Assert.Equal(appliedResponses[0], appliedResponses[1]);
        await using (var appliedScope = factory.Services.CreateAsyncScope())
        {
            var db = appliedScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var acknowledgement = await db.AgentConfigurationAcknowledgements.AsNoTracking().SingleAsync(x => x.AgentId == enrolled.AgentId && x.Id == appliedId, ct);
            var boundary = await db.AgentConfigurationEffectiveBoundaries.AsNoTracking().SingleAsync(x => x.AgentId == enrolled.AgentId && x.ConfigurationVersion == rollback.ConfigurationVersion, ct);
            Assert.Equal(acknowledgement.ReceivedAt, boundary.AppliedAcknowledgementReceivedAt);
            Assert.NotEqual(applicationNow, acknowledgement.ReceivedAt);
            Assert.NotEqual(applicationNow, boundary.AppliedAcknowledgementReceivedAt);
            Assert.NotEqual(agentAppliedAt, acknowledgement.ReceivedAt);
            Assert.NotEqual(agentAppliedAt, boundary.AppliedAcknowledgementReceivedAt);
            Assert.Equal(appliedId, boundary.SourceAcknowledgementId);
        }

        var laterApplied = await SendAgent<AgentConfigurationAcknowledgementResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/configuration/acknowledgements", enrolled.AgentCredential, new AgentConfigurationAcknowledgementRequest(1, Guid.NewGuid(), rollback.ConfigurationVersion, "Applied", agentAppliedAt.AddDays(1), null, DateTimeOffset.UtcNow), HttpStatusCode.OK, ct);
        Assert.Equal(rollback.ConfigurationVersion, laterApplied.ConfigurationVersion);
        await using var stableScope = factory.Services.CreateAsyncScope();
        var stableDb = stableScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var stableBoundary = await stableDb.AgentConfigurationEffectiveBoundaries.AsNoTracking().SingleAsync(x => x.AgentId == enrolled.AgentId && x.ConfigurationVersion == rollback.ConfigurationVersion, ct);
        Assert.Equal(appliedId, stableBoundary.SourceAcknowledgementId);

        var beforeBoundaryResultId = Guid.NewGuid();
        var equalBoundaryResultId = Guid.NewGuid();
        var afterBoundaryResultId = Guid.NewGuid();
        await using (var resultScope = factory.Services.CreateAsyncScope())
        {
            var db = resultScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            foreach (var (resultId, receivedAt) in new[]
            {
                (beforeBoundaryResultId, stableBoundary.AppliedAcknowledgementReceivedAt.Add(TimeSpan.FromMicroseconds(-1))),
                (equalBoundaryResultId, stableBoundary.AppliedAcknowledgementReceivedAt),
                (afterBoundaryResultId, stableBoundary.AppliedAcknowledgementReceivedAt.Add(TimeSpan.FromMicroseconds(1)))
            })
                db.Add(new ProbeResultLedgerEntry(enrolled.AgentId, resultId, firstProbeId, rollback.ConfigurationVersion, receivedAt.AddSeconds(-1), receivedAt, 3, 3, 0m, 1m, 1m, 1m, null, new byte[32], receivedAt));
            await db.SaveChangesAsync(ct);
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(stableBoundary.AppliedAcknowledgementReceivedAt));
            for (var index = 0; index < 3; index++) await processor.ProcessNextAsync(firstProbeId, ct);
        }
        await using (var processorScope = factory.Services.CreateAsyncScope())
        {
            var db = processorScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var dispositions = await db.ProbeResultProcessingDispositions.AsNoTracking().Where(x => x.AgentId == enrolled.AgentId).ToDictionaryAsync(x => x.ResultId, ct);
            Assert.Equal("config-not-effective", dispositions[beforeBoundaryResultId].ReasonCode);
            Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, dispositions[equalBoundaryResultId].Disposition);
            Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, dispositions[afterBoundaryResultId].Disposition);
            Assert.Equal(firstPolicyId, dispositions[equalBoundaryResultId].ResolvedPolicySnapshotId);
            Assert.Equal(firstPolicyId, dispositions[afterBoundaryResultId].ResolvedPolicySnapshotId);
        }

        var currentAgent = await SendAdmin<AgentResponse>(client, HttpMethod.Get, $"/api/v1/agents/{enrolled.AgentId}", null, ct);
        var concurrentVersion = await SendAdmin<AgentNetworkPolicyResponse>(
    client,
    HttpMethod.Put,
    $"/api/v1/agents/{enrolled.AgentId}/allowed-networks",
    new UpdateAgentAllowedNetworksRequest(
        1,
        ["192.0.2.0/24"],
        currentAgent.RowVersion),
    ct);
        var distinctAppliedIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var distinctResponses = await Task.WhenAll(distinctAppliedIds.Select(id => SendAgent<AgentConfigurationAcknowledgementResponse>(client, HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/configuration/acknowledgements", enrolled.AgentCredential, new AgentConfigurationAcknowledgementRequest(1, id, concurrentVersion.ConfigurationVersion, "Applied", agentAppliedAt, null, DateTimeOffset.UtcNow), HttpStatusCode.OK, ct)));
        Assert.All(distinctResponses, response => Assert.Equal(concurrentVersion.ConfigurationVersion, response.ConfigurationVersion));
        await using var concurrentScope = factory.Services.CreateAsyncScope();
        var concurrentDb = concurrentScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var concurrentBoundary = await concurrentDb.AgentConfigurationEffectiveBoundaries.AsNoTracking().SingleAsync(x => x.AgentId == enrolled.AgentId && x.ConfigurationVersion == concurrentVersion.ConfigurationVersion, ct);
        Assert.Contains(concurrentBoundary.SourceAcknowledgementId, distinctAppliedIds);
        var sourceAcknowledgement = await concurrentDb.AgentConfigurationAcknowledgements.AsNoTracking().SingleAsync(x => x.AgentId == enrolled.AgentId && x.Id == concurrentBoundary.SourceAcknowledgementId, ct);
        Assert.Equal(sourceAcknowledgement.ReceivedAt, concurrentBoundary.AppliedAcknowledgementReceivedAt);
    }

    private sealed class FixedClock(DateTimeOffset now) : IUtcClock { public DateTimeOffset UtcNow => now; }

    [Fact]
    public async Task St10bH2HeartbeatMaterializesOneExactCausePerEligibleProjection()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient();
        var fixture = await CreateSt10bHeartbeatFixtureAsync(factory, client, true, ct);
        var before = await ReadPostgresClockAsync(postgres.ConnectionString, ct);
        var heartbeatId = Guid.Parse("90000000-0000-0000-0000-000000000001"); var request = Heartbeat(heartbeatId);
        var response = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, request, HttpStatusCode.OK, ct);
        var after = await ReadPostgresClockAsync(postgres.ConnectionString, ct);
        await using var verifyScope = factory.Services.CreateAsyncScope(); var db = verifyScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var receipt = await db.AgentHeartbeatReceipts.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.HeartbeatId == heartbeatId, ct);
        var agent = await db.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, ct);
        var causes = await db.ProbeHeartbeatExpiryCauses.AsNoTracking().OrderBy(x => x.ProbeId.ToString()).ToArrayAsync(ct);
        Assert.Equal(response.ReceivedAt, agent.LastHeartbeatAt); Assert.Equal(response.ReceivedAt, receipt.ReceivedAt); Assert.Equal(2, causes.Length);
        // This is a deterministic non-canonical fixture set, not evidence of runtime lock order.
        // Canonical Agent/sorted-Probe lock acquisition is proven by the physical pg_locks contention test in T4.
        Assert.Equal(fixture.Probes.Select(x => x.ProbeId).OrderBy(x => x.ToString("D"), StringComparer.Ordinal), causes.Select(x => x.ProbeId).OrderBy(x => x.ToString("D"), StringComparer.Ordinal));
        Assert.All(causes, cause =>
        {
            var source = fixture.Probes.Single(x => x.ProbeId == cause.ProbeId);
            Assert.Equal((fixture.AgentId, source.ResultId, source.EventAt, response.ReceivedAt, 20, 100L, fixture.GroupId, fixture.PolicyId, 1, ProbeResultProcessingDispositionKind.StateDriving),
                (cause.AuthorityAgentId, cause.SourceResultId, cause.SourceCursorEventAt, cause.SourceLastHeartbeatReceivedAt, cause.SourceHeartbeatIntervalSeconds, cause.SourceConfigurationVersion, cause.SourceAgentGroupId, cause.PolicySnapshotId, cause.PolicyVersion, cause.SourceDisposition));
            Assert.Equal(response.ReceivedAt.AddSeconds(60), cause.DueAt); Assert.InRange(cause.RequestedAt, before, after); Assert.NotEqual(Guid.Empty, cause.CauseId);
        });
    }

    [Fact]
    public async Task St10bH2HeartbeatWithoutOwnedProjectionPersistsReceiptWithoutCause()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient();
        var fixture = await CreateSt10bHeartbeatFixtureAsync(factory, client, false, ct); var heartbeatId = Guid.Parse("90000000-0000-0000-0000-000000000002"); var request = Heartbeat(heartbeatId);
        var response = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, request, HttpStatusCode.OK, ct);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var agent = await db.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, ct); var receipt = await db.AgentHeartbeatReceipts.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.HeartbeatId == heartbeatId, ct);
        Assert.Equal((fixture.AgentId, "st10b-agent", "1.2.3", AgentSelfHealth.Healthy, AgentStatus.Online, 0L, 20, request.SentAt, response.ReceivedAt, false),
            (agent.Id, agent.MachineName, agent.AgentVersion, agent.SelfHealth, agent.Status, agent.QueueDepth, agent.HeartbeatIntervalSeconds, agent.LastReportedAt, agent.LastHeartbeatAt, agent.ClockSkewSuspected));
        Assert.Equal((fixture.AgentId, heartbeatId, response.ReceivedAt), (receipt.AgentId, receipt.HeartbeatId, receipt.ReceivedAt));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(response, AgentJson), receipt.ResponseJson);
        Assert.False(await db.ProbeHeartbeatExpiryCauses.AsNoTracking().AnyAsync(x => x.AuthorityAgentId == fixture.AgentId, ct));
    }

    [Fact]
    public async Task St10bH2DuplicateHeartbeatReplayPreservesAgentReceiptAndCauses()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct); await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient();
        var fixture = await CreateSt10bHeartbeatFixtureAsync(factory, client, true, ct); var heartbeatId = Guid.Parse("90000000-0000-0000-0000-000000000011"); var request = Heartbeat(heartbeatId);
        var first = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, request, HttpStatusCode.OK, ct); var before = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct);
        var replay = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, request, HttpStatusCode.OK, ct); var after = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct);
        Assert.Equal(first, replay); Assert.Equal(before.Agent, after.Agent); Assert.True(before.Receipts.SequenceEqual(after.Receipts)); Assert.True(before.Causes.SequenceEqual(after.Causes)); Assert.Equal(1, after.Receipts.Count(x => x.HeartbeatId == heartbeatId)); Assert.Equal(2, after.Causes.Length);
    }

    [Fact]
    public async Task St10bH2LaterHeartbeatCreatesImmutableSuccessorGenerationAndReplayIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct); await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient();
        var fixture = await CreateSt10bHeartbeatFixtureAsync(factory, client, true, ct); var firstRequest = Heartbeat(Guid.Parse("90000000-0000-0000-0000-000000000021")) with { SentAt = NormalizeMicroseconds(DateTimeOffset.UtcNow) };
        _ = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, firstRequest, HttpStatusCode.OK, ct); var first = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct); var originalReceipt = Assert.Single(first.Receipts, x => x.HeartbeatId == firstRequest.HeartbeatId); var beforeClock = await ReadPostgresClockAfterAsync(postgres.ConnectionString, first.Agent.LastHeartbeatAt!.Value, ct);
        var laterRequest = Heartbeat(Guid.Parse("90000000-0000-0000-0000-000000000022")) with { SentAt = NormalizeMicroseconds(firstRequest.SentAt.Add(TimeSpan.FromMicroseconds(1))) }; Assert.True(laterRequest.SentAt > firstRequest.SentAt); Assert.Equal(0, firstRequest.SentAt.UtcTicks % 10); Assert.Equal(0, laterRequest.SentAt.UtcTicks % 10); var later = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, laterRequest, HttpStatusCode.OK, ct); var afterClock = await ReadPostgresClockAsync(postgres.ConnectionString, ct); var advanced = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct);
        var preservedReceipt = Assert.Single(advanced.Receipts, x => x.HeartbeatId == firstRequest.HeartbeatId); var laterReceipt = Assert.Single(advanced.Receipts, x => x.HeartbeatId == laterRequest.HeartbeatId); Assert.Equal(originalReceipt, preservedReceipt); Assert.Equal((fixture.AgentId, laterRequest.HeartbeatId, later.ReceivedAt, System.Text.Json.JsonSerializer.Serialize(later, AgentJson)), (laterReceipt.AgentId, laterReceipt.HeartbeatId, laterReceipt.ReceivedAt, laterReceipt.ResponseJson)); Assert.Equal(later.ReceivedAt, advanced.Agent.LastHeartbeatAt); Assert.True(advanced.Agent.LastHeartbeatAt > first.Agent.LastHeartbeatAt); Assert.Equal(laterReceipt.ReceivedAt, advanced.Agent.LastHeartbeatAt); Assert.Equal(0, advanced.Agent.LastHeartbeatAt!.Value.UtcTicks % 10); Assert.Equal(2, advanced.Receipts.Length); Assert.True(first.Causes.SequenceEqual(advanced.Causes.Where(x => x.SourceLastHeartbeatAt == first.Agent.LastHeartbeatAt).OrderBy(x => x.ProbeId).ToArray()));
        var successors = advanced.Causes.Where(x => x.SourceLastHeartbeatAt == advanced.Agent.LastHeartbeatAt).OrderBy(x => x.ProbeId).ToArray(); Assert.Equal(2, successors.Length); Assert.Equal(fixture.Probes.Select(x => x.ProbeId).OrderBy(x => x), successors.Select(x => x.ProbeId)); Assert.All(successors, cause => { var source = Assert.Single(fixture.Probes, x => x.ProbeId == cause.ProbeId); Assert.Equal((source.ProbeId, source.ResultId, source.EventAt, fixture.AgentId, 100L, fixture.GroupId, fixture.PolicyId, 1), (cause.ProbeId, cause.SourceResultId, cause.SourceCursorEventAt, cause.AuthorityAgentId, cause.SourceConfigurationVersion, cause.SourceAgentGroupId, cause.PolicySnapshotId, cause.PolicyVersion)); Assert.Equal((fixture.AgentId, 20, ProbeResultProcessingDispositionKind.StateDriving), (cause.AuthorityAgentId, cause.SourceHeartbeatIntervalSeconds, cause.SourceDisposition)); Assert.Equal(advanced.Agent.LastHeartbeatAt.Value.AddSeconds(60), cause.DueAt); Assert.InRange(cause.RequestedAt, beforeClock, afterClock); Assert.DoesNotContain(first.Causes, prior => prior.CauseId == cause.CauseId); });
        _ = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, laterRequest, HttpStatusCode.OK, ct); var replay = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct); Assert.Equal(advanced.Agent, replay.Agent); Assert.True(advanced.Receipts.SequenceEqual(replay.Receipts)); Assert.True(advanced.Causes.SequenceEqual(replay.Causes)); Assert.Equal(originalReceipt, Assert.Single(replay.Receipts, x => x.HeartbeatId == firstRequest.HeartbeatId)); Assert.Equal(laterReceipt, Assert.Single(replay.Receipts, x => x.HeartbeatId == laterRequest.HeartbeatId));
    }

    [Fact]
    public async Task St10bH2FinalCauseFlushFailureRollsBackThenRetryAndReplayAreCoherent()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct); await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)); using var client = factory.CreateClient(); var fixture = await CreateSt10bHeartbeatFixtureAsync(factory, client, true, ct); var baseline = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct); var request = Heartbeat(Guid.Parse("90000000-0000-0000-0000-000000000031")); var resource = await InstallSt10bFailureTriggerAsync(postgres.ConnectionString, fixture.AgentId, ct); Exception? primary = null;
        try
        {
            using var failedRequest = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, AgentContent(request)); var failed = await client.SendAsync(failedRequest, ct); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); Assert.Contains(St10bFailureSignature, await failed.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await DropSt10bFailureTriggerAsync(postgres.ConnectionString, resource, cleanup.Token); }
            catch (Exception cleanupFailure) when (primary is not null) { primary.Data["St10bH2CleanupFailure"] = cleanupFailure; }
        }
        var rolledBack = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct); Assert.Equal(baseline.Agent, rolledBack.Agent); Assert.True(baseline.Receipts.SequenceEqual(rolledBack.Receipts)); Assert.True(baseline.Causes.SequenceEqual(rolledBack.Causes));
        var retryBefore = await ReadPostgresClockAsync(postgres.ConnectionString, ct); var success = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, request, HttpStatusCode.OK, ct); var retryAfter = await ReadPostgresClockAsync(postgres.ConnectionString, ct); var retry = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct); Assert.Equal(success.ReceivedAt, retry.Agent.LastHeartbeatAt); Assert.Single(retry.Receipts, x => x.HeartbeatId == request.HeartbeatId); Assert.Equal(2, retry.Causes.Length); Assert.Equal(2, retry.Causes.Select(x => x.CauseId).Distinct().Count()); Assert.Equal(fixture.Probes.Select(x => x.ProbeId).OrderBy(x => x), retry.Causes.Select(x => x.ProbeId).OrderBy(x => x)); Assert.All(retry.Causes, x => { var source = Assert.Single(fixture.Probes, probe => probe.ProbeId == x.ProbeId); Assert.NotEqual(Guid.Empty, x.CauseId); Assert.Equal((source.ProbeId, fixture.AgentId, source.ResultId, source.EventAt, success.ReceivedAt, 20, 100L, fixture.GroupId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving), (x.ProbeId, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.PolicySnapshotId, x.PolicyVersion, x.CauseType, x.SourceDisposition)); Assert.Equal(success.ReceivedAt.AddSeconds(60), x.DueAt); Assert.InRange(x.RequestedAt, retryBefore, retryAfter); });
        var replayResponse = await SendAgent<AgentHeartbeatResponse>(client, HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, request, HttpStatusCode.OK, ct); var replay = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct); Assert.Equal(success, replayResponse); Assert.Equal(retry.Agent, replay.Agent); Assert.True(retry.Receipts.SequenceEqual(replay.Receipts)); Assert.True(retry.Causes.SequenceEqual(replay.Causes));
    }

    [Fact]
    public async Task T4A2H2AcquiresReceivingAgentBeforeRequestingCanonicalProbeLock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var applicationName = $"t4a2-h2-{Guid.NewGuid():N}";
        var h2ConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { ApplicationName = applicationName }.ConnectionString;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", h2ConnectionString));
        using var client = factory.CreateClient();
        var fixture = await CreateSt10bHeartbeatFixtureAsync(factory, client, true, ct, eligibleProjectionCount: 1);
        var source = Assert.Single(fixture.Probes, x => x.HasProjection);
        var baseline = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct);
        St10bProjectionSnapshot projectionBefore;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            projectionBefore = await db.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == source.ProbeId)
                .Select(x => new St10bProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId, x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId)).SingleAsync(ct);
            Assert.Single(await db.ProbeStatusProjections.AsNoTracking().Where(x => x.WatermarkAgentId == fixture.AgentId).ToArrayAsync(ct));
        }
        Assert.Empty(baseline.Receipts); Assert.Empty(baseline.Causes);

        var heartbeatId = Guid.Parse("92000000-0000-0000-0000-000000000001");
        var requestBody = Heartbeat(heartbeatId) with { SentAt = NormalizeMicroseconds(DateTimeOffset.UtcNow) };
        var before = await ReadPostgresClockAsync(postgres.ConnectionString, ct);
        await using var blockerA = new NpgsqlConnection(postgres.ConnectionString); await blockerA.OpenAsync(ct); await using var txA = await blockerA.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var pidA = await GetBackendPidAsync(blockerA, ct); await AcquireProbeAdvisoryLockAsync(blockerA, txA, source.ProbeId, ct);
        await using var observer = new NpgsqlConnection(postgres.ConnectionString); await observer.OpenAsync(ct); var observerPid = await GetBackendPidAsync(observer, ct);
        var blockerC = new NpgsqlConnection(postgres.ConnectionString); await blockerC.OpenAsync(ct); var txC = await blockerC.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct); var pidC = await GetBackendPidAsync(blockerC, ct);
        HttpRequestMessage? heartbeatRequest = null; Task<HttpResponseMessage>? heartbeatTask = null; HttpResponseMessage? heartbeatResponse = null; Task? waitC = null; NpgsqlCommand? waitCCommand = null; CancellationTokenSource? waitCCancellation = null; int? pidB = null; Exception? primary = null; var releasedA = false; var releasedC = false;
        try
        {
            heartbeatRequest = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, AgentContent(requestBody));
            heartbeatTask = client.SendAsync(heartbeatRequest, ct);
            pidB = await WaitForApplicationBackendAsync(observer, applicationName, heartbeatTask, ct);
            Assert.NotEqual(pidA, pidB.Value); Assert.NotEqual(pidC, pidB.Value); Assert.NotEqual(observerPid, pidB.Value); Assert.NotEqual(pidA, pidC); Assert.NotEqual(pidA, observerPid); Assert.NotEqual(pidC, observerPid);
            await WaitForProbeAdvisoryWaitAsync(observer, pidB.Value, pidA, source.ProbeId, heartbeatTask, ct);
            waitCCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); waitCCancellation.CancelAfter(TimeSpan.FromSeconds(10));
            waitC = LockAgentForShareWithoutFollowUpCommandAsync(blockerC, txC, fixture.AgentId, command => waitCCommand = command, waitCCancellation.Token);
            await WaitForAgentShareWaitAsync(observer, pidC, pidB.Value, pidA, waitC, ct);
            await txA.RollbackAsync(ct); releasedA = true;
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct)) { bounded.CancelAfter(TimeSpan.FromSeconds(10)); heartbeatResponse = await heartbeatTask.WaitAsync(bounded.Token); await waitC.WaitAsync(bounded.Token); }
            await txC.RollbackAsync(ct); releasedC = true;
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task AttemptAsync(string name, Func<CancellationToken, Task> action) { try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await action(cleanup.Token); } catch (Exception failure) { cleanupFailures.Add(new InvalidOperationException($"T4A2 cleanup failed while {name}.", failure)); } }
            if (!releasedA) await AttemptAsync("releasing blocker A", async token => await txA.RollbackAsync(token));
            if (heartbeatTask is not null && !await ObserveT4A2TerminalAsync("H2 request", heartbeatTask, false, cleanupFailures))
            {
                if (pidB is not null) await AttemptAsync("canceling H2 backend", token => SignalBackendAsync(observer, pidB.Value, false, token));
                if (!await ObserveT4A2TerminalAsync("H2 request after cancellation", heartbeatTask, true, cleanupFailures) && pidB is not null)
                { await AttemptAsync("terminating H2 backend", token => SignalBackendAsync(observer, pidB.Value, true, token)); await ObserveT4A2TerminalAsync("H2 request after termination", heartbeatTask, true, cleanupFailures); }
            }
            if (waitC is not null && !await ObserveT4A2TerminalAsync("blocker C FOR SHARE", waitC, false, cleanupFailures))
            {
                try { waitCCancellation?.Cancel(); } catch (Exception failure) { cleanupFailures.Add(new InvalidOperationException("T4A2 cleanup failed while canceling blocker C token.", failure)); }
                try { waitCCommand?.Cancel(); } catch (Exception failure) { cleanupFailures.Add(new InvalidOperationException("T4A2 cleanup failed while canceling blocker C command.", failure)); }
                if (!await ObserveT4A2TerminalAsync("blocker C after command cancellation", waitC, true, cleanupFailures))
                { await AttemptAsync("canceling blocker C backend", token => SignalBackendAsync(observer, pidC, false, token)); if (!await ObserveT4A2TerminalAsync("blocker C after backend cancellation", waitC, true, cleanupFailures)) { await AttemptAsync("terminating blocker C backend", token => SignalBackendAsync(observer, pidC, true, token)); await ObserveT4A2TerminalAsync("blocker C after backend termination", waitC, true, cleanupFailures); } }
            }
            if (waitC is null || waitC.IsCompleted) { if (!releasedC) await AttemptAsync("releasing blocker C", async token => await txC.RollbackAsync(token)); await AttemptAsync("disposing blocker C transaction", async _ => await txC.DisposeAsync()); await AttemptAsync("disposing blocker C connection", async _ => await blockerC.DisposeAsync()); }
            else cleanupFailures.Add(new InvalidOperationException("T4A2 cleanup left blocker C undisposed because its command was not terminal."));
            if (heartbeatTask is null || heartbeatTask.IsCompleted) heartbeatRequest?.Dispose();
            waitCCancellation?.Dispose();
            if (cleanupFailures.Count == 0) { }
            else if (primary is not null) for (var index = 0; index < cleanupFailures.Count; index++) primary.Data[$"T4A2CleanupFailure{index + 1}"] = cleanupFailures[index];
            else if (cleanupFailures.Count == 1) throw cleanupFailures[0]; else throw new AggregateException(cleanupFailures);
        }

        var after = await ReadPostgresClockAsync(postgres.ConnectionString, ct);
        Assert.NotNull(heartbeatResponse); Assert.Equal(HttpStatusCode.OK, heartbeatResponse.StatusCode);
        var response = (await heartbeatResponse.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(ct))!;
        Assert.Equal((heartbeatId, fixture.AgentId, 20), (response.HeartbeatId, response.AgentId, response.NextHeartbeatSeconds)); Assert.InRange(response.ReceivedAt, before, after);
        var post = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct);
        var receipt = Assert.Single(post.Receipts, x => x.HeartbeatId == heartbeatId); var cause = Assert.Single(post.Causes);
        Assert.Equal((fixture.AgentId, heartbeatId, response.ReceivedAt, System.Text.Json.JsonSerializer.Serialize(response, AgentJson)), (receipt.AgentId, receipt.HeartbeatId, receipt.ReceivedAt, receipt.ResponseJson));
        Assert.Equal(response.ReceivedAt, post.Agent.LastHeartbeatAt); Assert.Equal(20, post.Agent.IntervalSeconds); Assert.Equal(requestBody.SentAt, post.Agent.LastReportedAt);
        Assert.Equal((baseline.Agent.Id, baseline.Agent.Version, baseline.Agent.Machine, baseline.Agent.Health, baseline.Agent.Status, baseline.Agent.QueueDepth, baseline.Agent.IntervalSeconds, baseline.Agent.ClockSkewSuspected), (post.Agent.Id, post.Agent.Version, post.Agent.Machine, post.Agent.Health, post.Agent.Status, post.Agent.QueueDepth, post.Agent.IntervalSeconds, post.Agent.ClockSkewSuspected));
        Assert.NotEqual(Guid.Empty, cause.CauseId); Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, source.ProbeId, fixture.AgentId, source.ResultId, source.EventAt, response.ReceivedAt, 20, 100L, fixture.GroupId, fixture.PolicyId, 1), (cause.CauseType, cause.SourceDisposition, cause.ProbeId, cause.AuthorityAgentId, cause.SourceResultId, cause.SourceCursorEventAt, cause.SourceLastHeartbeatAt, cause.SourceHeartbeatIntervalSeconds, cause.SourceConfigurationVersion, cause.SourceAgentGroupId, cause.PolicySnapshotId, cause.PolicyVersion)); Assert.Equal(response.ReceivedAt.AddSeconds(60), cause.DueAt); Assert.InRange(cause.RequestedAt, before, after);
        await using var verifyScope = factory.Services.CreateAsyncScope(); var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var projectionAfter = await verifyDb.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == source.ProbeId).Select(x => new St10bProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId, x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId)).SingleAsync(ct);
        Assert.Equal(projectionBefore, projectionAfter); Assert.Single(post.Receipts); Assert.Single(post.Causes); Assert.True(baseline.Receipts.SequenceEqual(post.Receipts.Where(x => x.HeartbeatId != heartbeatId))); Assert.True(baseline.Causes.SequenceEqual(post.Causes.Where(x => x.SourceLastHeartbeatAt != response.ReceivedAt)));
    }

    [Fact]
    public async Task T4B1H1FirstAppliesThenWaitingH2CreatesSuccessorGeneration()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var h1ApplicationName = $"t4b1-h1-{Guid.NewGuid():N}"; var h2ApplicationName = $"t4b1-h2-{Guid.NewGuid():N}";
        var h1ConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { ApplicationName = h1ApplicationName }.ConnectionString;
        var h2ConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { ApplicationName = h2ApplicationName }.ConnectionString;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", h2ConnectionString)); using var client = factory.CreateClient();
        var fixture = await CreateSt10bHeartbeatFixtureAsync(factory, client, true, ct, eligibleProjectionCount: 1); var initialSource = Assert.Single(fixture.Probes, x => x.HasProjection);
        var oldHeartbeat = (await ReadPostgresClockAsync(postgres.ConnectionString, ct)).AddMinutes(-2); var sourceResultId = Guid.Parse("93000000-0000-0000-0000-000000000001"); var sourceEventAt = oldHeartbeat.AddSeconds(30);
        var causeCreatedBefore = await ReadPostgresClockAsync(postgres.ConnectionString, ct);
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<EePulseDbContext>(); var agent = await db.Agents.SingleAsync(x => x.Id == fixture.AgentId, ct);
            agent.Heartbeat(agent.AgentVersion, agent.MachineName, agent.QueueDepth, agent.SelfHealth, agent.DesiredConfigurationVersion, oldHeartbeat, oldHeartbeat);
            db.Add(new ProbeResultLedgerEntry(fixture.AgentId, sourceResultId, initialSource.ProbeId, 100, sourceEventAt.AddSeconds(-1), sourceEventAt, 1, 1, 0m, 1m, 1m, 1m, null, new byte[32], sourceEventAt)); await db.SaveChangesAsync(ct);
            await new ProbeResultStatusProcessor(db, new FixedClock(sourceEventAt)).ProcessNextAsync(initialSource.ProbeId, ct);
        }
        var causeCreatedAfter = await ReadPostgresClockAsync(postgres.ConnectionString, ct);
        var preRace = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct); var originalCause = Assert.Single(preRace.Causes); var before = await ReadPostgresClockAsync(postgres.ConnectionString, ct); Assert.True(originalCause.DueAt <= before);
        St10bProjectionSnapshot preRaceProjection; St10bCauseSnapshot originalCauseSnapshot; St10bArtifactSnapshot artifactsBefore;
        await using (var preScope = factory.Services.CreateAsyncScope())
        {
            var db = preScope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var agent = await db.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, ct);
            var boundary = await db.AgentConfigurationEffectiveBoundaries.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ConfigurationVersion == 100, ct);
            originalCauseSnapshot = await db.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.CauseId == originalCause.CauseId).Select(x => new St10bCauseSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).SingleAsync(ct);
            preRaceProjection = await db.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == initialSource.ProbeId).Select(x => new St10bProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId, x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId)).SingleAsync(ct);
            Assert.Equal((fixture.AgentId, fixture.GroupId, oldHeartbeat, 20), (agent.Id, agent.AgentGroupId, agent.LastHeartbeatAt, agent.HeartbeatIntervalSeconds)); Assert.Equal(100, boundary.ConfigurationVersion);
            Assert.Equal((originalCause.CauseId, ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, initialSource.ProbeId, fixture.AgentId, sourceResultId, sourceEventAt, oldHeartbeat, 20, 100L, fixture.GroupId, fixture.PolicyId, 1, oldHeartbeat.AddSeconds(60)), (originalCauseSnapshot.CauseId, originalCauseSnapshot.CauseType, originalCauseSnapshot.SourceDisposition, originalCauseSnapshot.ProbeId, originalCauseSnapshot.AuthorityAgentId, originalCauseSnapshot.SourceResultId, originalCauseSnapshot.SourceCursorEventAt, originalCauseSnapshot.SourceLastHeartbeatAt, originalCauseSnapshot.SourceHeartbeatIntervalSeconds, originalCauseSnapshot.SourceConfigurationVersion, originalCauseSnapshot.SourceAgentGroupId, originalCauseSnapshot.PolicySnapshotId, originalCauseSnapshot.PolicyVersion, originalCauseSnapshot.DueAt)); Assert.NotEqual(Guid.Empty, originalCauseSnapshot.CauseId); Assert.InRange(originalCauseSnapshot.RequestedAt, causeCreatedBefore, causeCreatedAfter);
            Assert.Equal((initialSource.ProbeId, ProbeStatus.Up, ProbeStatus.Up, fixture.AgentId, sourceResultId, sourceEventAt, sourceEventAt, 0, 2, 1L, (Guid?)null), (preRaceProjection.ProbeId, preRaceProjection.UnderlyingStatus, preRaceProjection.VisibleStatus, preRaceProjection.WatermarkAgentId, preRaceProjection.WatermarkResultId, preRaceProjection.WatermarkEventAt, preRaceProjection.LastFreshEventAt, preRaceProjection.ConsecutiveFailureCount, preRaceProjection.ConsecutiveSuccessCount, preRaceProjection.StateVersion, preRaceProjection.OpenIncidentId));
            Assert.False(await db.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == originalCause.CauseId, ct)); Assert.False(await db.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().AnyAsync(x => x.CauseId == originalCause.CauseId, ct)); Assert.Equal(1, await db.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == initialSource.ProbeId && x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == sourceResultId, ct));
            artifactsBefore = await ReadT4B1ArtifactsAsync(db, initialSource.ProbeId, ct);
            Assert.NotEmpty(artifactsBefore.ResultDispositions); Assert.Empty(artifactsBefore.ResultTransitions); Assert.Empty(artifactsBefore.Incidents); Assert.Empty(artifactsBefore.Events); Assert.Empty(artifactsBefore.Contexts);
        }
        var heartbeatId = Guid.Parse("93000000-0000-0000-0000-000000000002"); var requestBody = Heartbeat(heartbeatId) with { SentAt = NormalizeMicroseconds(DateTimeOffset.UtcNow) };
        await using var blockerA = new NpgsqlConnection(postgres.ConnectionString); await blockerA.OpenAsync(ct); await using var txA = await blockerA.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct); var pidA = await GetBackendPidAsync(blockerA, ct); await AcquireProbeAdvisoryLockAsync(blockerA, txA, initialSource.ProbeId, ct);
        await using var observer = new NpgsqlConnection(postgres.ConnectionString); await observer.OpenAsync(ct); var observerPid = await GetBackendPidAsync(observer, ct);
        var h1Db = new EePulseDbContext(new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(h1ConnectionString).Options); HttpRequestMessage? h2Request = null; Task<ProbeHeartbeatExpiryProcessorOutcome>? h1Task = null; Task<HttpResponseMessage>? h2Task = null; HttpResponseMessage? h2Response = null; int? pidB = null; int? pidC = null; Exception? primary = null; var releasedA = false;
        try
        {
            h1Task = new ProbeHeartbeatExpiryCauseProcessor(h1Db).ProcessNextDueAsync(initialSource.ProbeId, ct); pidB = await WaitForApplicationBackendAsync(observer, h1ApplicationName, h1Task, ct); Assert.NotEqual(pidA, pidB.Value); Assert.NotEqual(observerPid, pidB.Value);
            await WaitForProbeAdvisoryWaitAsync(observer, pidB.Value, pidA, initialSource.ProbeId, h1Task, ct);
            await using (var blockedScope = factory.Services.CreateAsyncScope()) { var db = blockedScope.ServiceProvider.GetRequiredService<EePulseDbContext>(); Assert.False(await db.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == originalCause.CauseId, ct)); }
            h2Request = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, AgentContent(requestBody)); h2Task = client.SendAsync(h2Request, ct); pidC = await WaitForApplicationBackendAsync(observer, h2ApplicationName, h2Task, ct); Assert.NotEqual(pidA, pidC.Value); Assert.NotEqual(pidB.Value, pidC.Value); Assert.NotEqual(observerPid, pidC.Value);
            await WaitForAgentShareWaitAsync(observer, pidC.Value, pidB.Value, pidA, h2Task, ct);
            await txA.RollbackAsync(ct); releasedA = true;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(10)); var h1 = await h1Task.WaitAsync(bounded.Token); h2Response = await h2Task.WaitAsync(bounded.Token);
            Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.Applied, originalCause.CauseId), (h1.Kind, h1.CauseId));
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>(); async Task AttemptAsync(string name, Func<CancellationToken, Task> action) { try { using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await action(token.Token); } catch (Exception failure) { cleanupFailures.Add(new InvalidOperationException($"T4B1 cleanup failed while {name}.", failure)); } }
            if (!releasedA) await AttemptAsync("releasing blocker A", async token => await txA.RollbackAsync(token));
            if (h1Task is not null && !await ObserveT4A2TerminalAsync("H1", h1Task, false, cleanupFailures) && pidB is not null) { await AttemptAsync("canceling H1 backend", token => SignalBackendAsync(observer, pidB.Value, false, token)); if (!await ObserveT4A2TerminalAsync("H1 after cancellation", h1Task, true, cleanupFailures)) { await AttemptAsync("terminating H1 backend", token => SignalBackendAsync(observer, pidB.Value, true, token)); await ObserveT4A2TerminalAsync("H1 after termination", h1Task, true, cleanupFailures); } }
            if (h2Task is not null && !await ObserveT4A2TerminalAsync("H2", h2Task, false, cleanupFailures) && pidC is not null) { await AttemptAsync("canceling H2 backend", token => SignalBackendAsync(observer, pidC.Value, false, token)); if (!await ObserveT4A2TerminalAsync("H2 after cancellation", h2Task, true, cleanupFailures)) { await AttemptAsync("terminating H2 backend", token => SignalBackendAsync(observer, pidC.Value, true, token)); await ObserveT4A2TerminalAsync("H2 after termination", h2Task, true, cleanupFailures); } }
            if (h1Task is null || h1Task.IsCompleted) await AttemptAsync("disposing H1 context", async _ => await h1Db.DisposeAsync()); else cleanupFailures.Add(new InvalidOperationException("T4B1 cleanup left the H1 context undisposed because H1 was not terminal.")); if (h2Task is null || h2Task.IsCompleted) h2Request?.Dispose();
            if (cleanupFailures.Count == 0) { } else if (primary is not null) for (var index = 0; index < cleanupFailures.Count; index++) primary.Data[$"T4B1CleanupFailure{index + 1}"] = cleanupFailures[index]; else if (cleanupFailures.Count == 1) throw cleanupFailures[0]; else throw new AggregateException(cleanupFailures);
        }
        var after = await ReadPostgresClockAsync(postgres.ConnectionString, ct); Assert.NotNull(h2Response); Assert.Equal(HttpStatusCode.OK, h2Response.StatusCode); var response = Assert.IsType<AgentHeartbeatResponse>(h2Payload);
        await using var verifyScope = factory.Services.CreateAsyncScope(); var verify = verifyScope.ServiceProvider.GetRequiredService<EePulseDbContext>(); var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == originalCause.CauseId, ct); var transition = await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().SingleAsync(x => x.CauseId == originalCause.CauseId, ct); var successor = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.AuthorityAgentId == fixture.AgentId && x.SourceLastHeartbeatReceivedAt == response.ReceivedAt, ct); var originalAfter = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.CauseId == originalCause.CauseId).Select(x => new St10bCauseSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).SingleAsync(ct); var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(x => x.ProbeId == initialSource.ProbeId, ct); var receipt = await verify.AgentHeartbeatReceipts.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.HeartbeatId == heartbeatId, ct); var persistedAgent = await verify.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, ct);
        Assert.Equal((ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode, fixture.PolicyId, 1, disposition.ExpiryCutoffReceivedAt, disposition.ExpiryCutoffReceivedAt), (disposition.Outcome, disposition.ReasonCode, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt)); Assert.InRange(disposition.AppliedAt!.Value, before, after);
        Assert.Equal((ProbeStatus.Up, ProbeStatus.Unknown, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode, disposition.AppliedAt), (transition.FromVisibleStatus, transition.ToVisibleStatus, transition.ReasonCode, transition.AppliedAt));
        Assert.Equal((fixture.AgentId, heartbeatId, response.ReceivedAt, System.Text.Json.JsonSerializer.Serialize(response, AgentJson)), (receipt.AgentId, receipt.HeartbeatId, receipt.ReceivedAt, receipt.ResponseJson)); Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, initialSource.ProbeId, fixture.AgentId, sourceResultId, sourceEventAt, response.ReceivedAt, 20, 100L, fixture.GroupId, fixture.PolicyId, 1, response.ReceivedAt.AddSeconds(60)), (successor.CauseType, successor.SourceDisposition, successor.ProbeId, successor.AuthorityAgentId, successor.SourceResultId, successor.SourceCursorEventAt, successor.SourceLastHeartbeatReceivedAt, successor.SourceHeartbeatIntervalSeconds, successor.SourceConfigurationVersion, successor.SourceAgentGroupId, successor.PolicySnapshotId, successor.PolicyVersion, successor.DueAt)); Assert.InRange(successor.RequestedAt, before, after); Assert.NotEqual(Guid.Empty, successor.CauseId); Assert.NotEqual(originalCause.CauseId, successor.CauseId); Assert.Equal((response.ReceivedAt, 20), (persistedAgent.LastHeartbeatAt, persistedAgent.HeartbeatIntervalSeconds));
        Assert.Equal(originalCauseSnapshot, originalAfter); Assert.Equal(1, await verify.AgentHeartbeatReceipts.AsNoTracking().CountAsync(x => x.AgentId == fixture.AgentId, ct)); Assert.Equal(preRace.Causes.Length + 1, await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == initialSource.ProbeId && x.AuthorityAgentId == fixture.AgentId, ct)); Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == initialSource.ProbeId && x.SourceLastHeartbeatReceivedAt == response.ReceivedAt, ct));
        var projectionAfter = new St10bProjectionSnapshot(projection.ProbeId, projection.UnderlyingStatus, projection.VisibleStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.StateVersion, projection.WatermarkAgentId, projection.WatermarkResultId, projection.WatermarkEventAt, projection.LastFreshEventAt, projection.OpenIncidentId); Assert.Equal(preRaceProjection with { VisibleStatus = ProbeStatus.Unknown, StateVersion = preRaceProjection.StateVersion + 1 }, projectionAfter); Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == initialSource.ProbeId, ct)); Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().CountAsync(x => x.ProbeId == initialSource.ProbeId, ct));
        var artifactsAfter = await ReadT4B1ArtifactsAsync(verify, initialSource.ProbeId, ct); Assert.True(artifactsBefore.ResultDispositions.SequenceEqual(artifactsAfter.ResultDispositions)); Assert.True(artifactsBefore.ResultTransitions.SequenceEqual(artifactsAfter.ResultTransitions)); Assert.True(artifactsBefore.Incidents.SequenceEqual(artifactsAfter.Incidents)); Assert.True(artifactsBefore.Events.SequenceEqual(artifactsAfter.Events)); Assert.True(artifactsBefore.Contexts.SequenceEqual(artifactsAfter.Contexts));
    }

    [Fact]
    public async Task T4B2H2FirstCreatesSuccessorThenWaitingH1RecordsHeartbeatAdvancedNoOp()
    {
        var ct = TestContext.Current.CancellationToken; await using var postgres = await PostgresTestDatabase.StartAsync(ct); var h2Name = $"t4b2-h2-{Guid.NewGuid():N}"; var h1Name = $"t4b2-h1-{Guid.NewGuid():N}";
        var h2Connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { ApplicationName = h2Name }.ConnectionString; var h1Connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { ApplicationName = h1Name }.ConnectionString;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", h2Connection)); var client = factory.CreateClient(); var fixture = await CreateSt10bHeartbeatFixtureAsync(factory, client, true, ct, eligibleProjectionCount: 1); var source = Assert.Single(fixture.Probes, x => x.HasProjection);
        var oldHeartbeat = (await ReadPostgresClockAsync(postgres.ConnectionString, ct)).AddMinutes(-2); var sourceResultId = Guid.Parse("94000000-0000-0000-0000-000000000001"); var sourceEventAt = oldHeartbeat.AddSeconds(30); var createdBefore = await ReadPostgresClockAsync(postgres.ConnectionString, ct);
        await using (var setup = factory.Services.CreateAsyncScope()) { var db = setup.ServiceProvider.GetRequiredService<EePulseDbContext>(); var agent = await db.Agents.SingleAsync(x => x.Id == fixture.AgentId, ct); agent.Heartbeat(agent.AgentVersion, agent.MachineName, agent.QueueDepth, agent.SelfHealth, agent.DesiredConfigurationVersion, oldHeartbeat, oldHeartbeat); db.Add(new ProbeResultLedgerEntry(fixture.AgentId, sourceResultId, source.ProbeId, 100, sourceEventAt.AddSeconds(-1), sourceEventAt, 1, 1, 0m, 1m, 1m, 1m, null, new byte[32], sourceEventAt)); await db.SaveChangesAsync(ct); await new ProbeResultStatusProcessor(db, new FixedClock(sourceEventAt)).ProcessNextAsync(source.ProbeId, ct); }
        var createdAfter = await ReadPostgresClockAsync(postgres.ConnectionString, ct); var baseline = await ReadSt10bHeartbeatSnapshotAsync(factory, fixture.AgentId, ct); var original = Assert.Single(baseline.Causes); var before = await ReadPostgresClockAsync(postgres.ConnectionString, ct); Assert.True(original.DueAt <= before); Assert.InRange(original.RequestedAt, createdBefore, createdAfter);
        var expectedProjectionBefore = new St10bProjectionSnapshot(source.ProbeId, ProbeStatus.Up, ProbeStatus.Up, 0, 2, 1L, fixture.AgentId, sourceResultId, sourceEventAt, sourceEventAt, null);
        St10bProjectionSnapshot projectionBefore; St10bCauseSnapshot originalSnapshot; St10bArtifactSnapshot artifactsBefore; int baselineH1DispositionCount;
        await using (var pre = factory.Services.CreateAsyncScope()) { var db = pre.ServiceProvider.GetRequiredService<EePulseDbContext>(); var agent = await db.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, ct); originalSnapshot = await db.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.CauseId == original.CauseId).Select(x => new St10bCauseSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).SingleAsync(ct); projectionBefore = await db.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == source.ProbeId).Select(x => new St10bProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId, x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId)).SingleAsync(ct); Assert.Equal(expectedProjectionBefore, projectionBefore); Assert.Equal((fixture.AgentId, fixture.GroupId, oldHeartbeat, 20), (agent.Id, agent.AgentGroupId, agent.LastHeartbeatAt, agent.HeartbeatIntervalSeconds)); Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, source.ProbeId, fixture.AgentId, sourceResultId, sourceEventAt, oldHeartbeat, 20, 100L, fixture.GroupId, fixture.PolicyId, 1, oldHeartbeat.AddSeconds(60)), (originalSnapshot.CauseType, originalSnapshot.SourceDisposition, originalSnapshot.ProbeId, originalSnapshot.AuthorityAgentId, originalSnapshot.SourceResultId, originalSnapshot.SourceCursorEventAt, originalSnapshot.SourceLastHeartbeatAt, originalSnapshot.SourceHeartbeatIntervalSeconds, originalSnapshot.SourceConfigurationVersion, originalSnapshot.SourceAgentGroupId, originalSnapshot.PolicySnapshotId, originalSnapshot.PolicyVersion, originalSnapshot.DueAt)); baselineH1DispositionCount = await db.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == source.ProbeId, ct); Assert.Equal(0, baselineH1DispositionCount); artifactsBefore = await ReadT4B1ArtifactsAsync(db, source.ProbeId, ct); var expectedResultDisposition = new St10bResultDispositionSnapshot(fixture.AgentId, sourceResultId, source.ProbeId, sourceEventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1, sourceEventAt); Assert.Equal(expectedResultDisposition, Assert.Single(artifactsBefore.ResultDispositions)); Assert.Empty(artifactsBefore.ResultTransitions); Assert.Empty(artifactsBefore.Incidents); Assert.Empty(artifactsBefore.Events); Assert.Empty(artifactsBefore.Contexts); }
        var heartbeatId = Guid.Parse("94000000-0000-0000-0000-000000000002"); var requestBody = Heartbeat(heartbeatId) with { SentAt = NormalizeMicroseconds(DateTimeOffset.UtcNow) };
        await using var blockerA = new NpgsqlConnection(postgres.ConnectionString); await blockerA.OpenAsync(ct); await using var txA = await blockerA.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct); var pidA = await GetBackendPidAsync(blockerA, ct); await AcquireProbeAdvisoryLockAsync(blockerA, txA, source.ProbeId, ct); await using var observer = new NpgsqlConnection(postgres.ConnectionString); await observer.OpenAsync(ct);
        var h1Db = new EePulseDbContext(new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(h1Connection).Options); HttpRequestMessage? h2Request = null; Task<HttpResponseMessage>? h2Task = null; Task<ProbeHeartbeatExpiryProcessorOutcome>? h1Task = null; HttpResponseMessage? h2Response = null; string? h2Payload = null; var h2Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); var h1Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); T4B2BackendIdentity? backendB = null; T4B2BackendIdentity? backendC = null; Exception? primary = null; var releasedA = false;
        try { h2Request = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{fixture.AgentId}/heartbeat", fixture.Credential, AgentContent(requestBody)); h2Task = client.SendAsync(h2Request, h2Cancellation.Token); backendB = await CaptureT4B2BackendIdentityAsync(observer, h2Name, h2Task, ct); await WaitForProbeAdvisoryWaitAsync(observer, backendB.Pid, pidA, source.ProbeId, h2Task, ct); await using (var blocked = factory.Services.CreateAsyncScope()) { var db = blocked.ServiceProvider.GetRequiredService<EePulseDbContext>(); var blockedAgent = await db.Agents.AsNoTracking().Where(x => x.Id == fixture.AgentId).Select(x => new St10bAgentSnapshot(x.Id, x.AgentVersion, x.MachineName, x.SelfHealth, x.Status, x.QueueDepth, x.LastHeartbeatAt, x.LastReportedAt, x.HeartbeatIntervalSeconds, x.ClockSkewSuspected, x.RowVersion)).SingleAsync(ct); var blockedProjection = await db.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == source.ProbeId).Select(x => new St10bProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId, x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId)).SingleAsync(ct); Assert.Equal(baseline.Agent, blockedAgent); Assert.Equal(projectionBefore, blockedProjection); Assert.Empty(await db.AgentHeartbeatReceipts.AsNoTracking().Where(x => x.AgentId == fixture.AgentId).ToArrayAsync(ct)); Assert.Equal(new[] { originalSnapshot }, await db.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.AuthorityAgentId == fixture.AgentId).Select(x => new St10bCauseSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).ToArrayAsync(ct)); var blockedArtifacts = await ReadT4B1ArtifactsAsync(db, source.ProbeId, ct); Assert.True(artifactsBefore.ResultDispositions.SequenceEqual(blockedArtifacts.ResultDispositions)); Assert.True(artifactsBefore.ResultTransitions.SequenceEqual(blockedArtifacts.ResultTransitions)); Assert.True(artifactsBefore.Incidents.SequenceEqual(blockedArtifacts.Incidents)); Assert.True(artifactsBefore.Events.SequenceEqual(blockedArtifacts.Events)); Assert.True(artifactsBefore.Contexts.SequenceEqual(blockedArtifacts.Contexts)); } h1Task = new ProbeHeartbeatExpiryCauseProcessor(h1Db).ProcessNextDueAsync(source.ProbeId, h1Cancellation.Token); backendC = await CaptureT4B2BackendIdentityAsync(observer, h1Name, h1Task, ct); await WaitForAgentShareWaitAsync(observer, backendC.Pid, backendB.Pid, pidA, h1Task, ct); await txA.RollbackAsync(ct); releasedA = true; using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(10)); h2Response = await h2Task.WaitAsync(bounded.Token); h2Payload = await h2Response.Content.ReadAsStringAsync(bounded.Token); var h1 = await h1Task.WaitAsync(bounded.Token); Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, original.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityHeartbeatAdvancedReasonCode), (h1.Kind, h1.CauseId, h1.DispositionOutcome, h1.ReasonCode)); }
        catch (Exception exception) { primary = exception; throw; }
        finally { var failures = new List<Exception>(); async Task Attempt(string name, Func<CancellationToken, Task> action) { try { using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await action(token.Token); } catch (Exception failure) { failures.Add(new InvalidOperationException($"T4B2 cleanup failed while {name}.", failure)); } } if (!releasedA) await Attempt("releasing blocker A", async t => await txA.RollbackAsync(t)); var h2Terminal = await SettleT4B2TaskAsync("H2", h2Task, h2Cancellation, backendB, observer, failures); var h1Terminal = await SettleT4B2TaskAsync("H1", h1Task, h1Cancellation, backendC, observer, failures); if (h1Terminal) { await Attempt("disposing H1 DbContext", async _ => await h1Db.DisposeAsync()); h1Cancellation.Dispose(); } else { await Attempt("transferring H1 ownership", _ => { TransferT4B2Ownership("H1", h1Task!, h1Cancellation, backendC, [new("H1 DbContext", async () => await h1Db.DisposeAsync())], failures, primary); return Task.CompletedTask; }); } if (h2Terminal) { await Attempt("disposing H2 request", _ => { h2Request?.Dispose(); return Task.CompletedTask; }); await Attempt("disposing H2 client", _ => { client.Dispose(); return Task.CompletedTask; }); await Attempt("disposing H2 factory", async _ => await factory.DisposeAsync()); h2Cancellation.Dispose(); } else { await Attempt("transferring H2 ownership", _ => { TransferT4B2Ownership("H2", h2Task!, h2Cancellation, backendB, [new("H2 request", () => { h2Request?.Dispose(); return Task.CompletedTask; }), new("H2 client", () => { client.Dispose(); return Task.CompletedTask; }), new("H2 factory", async () => await factory.DisposeAsync())], failures, primary); return Task.CompletedTask; }); } if (failures.Count == 0) { } else if (primary is not null) for (var i = 0; i < failures.Count; i++) primary.Data[$"T4B2CleanupFailure{i + 1}"] = failures[i]; else if (failures.Count == 1) throw failures[0]; else throw new AggregateException(failures); }
        var after = await ReadPostgresClockAsync(postgres.ConnectionString, ct); Assert.NotNull(h2Response); Assert.Equal(HttpStatusCode.OK, h2Response.StatusCode); var response = System.Text.Json.JsonSerializer.Deserialize<AgentHeartbeatResponse>(Assert.IsType<string>(h2Payload), AgentJson)!;
        await using var verify = new EePulseDbContext(new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options); var originalAfter = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.CauseId == original.CauseId).Select(x => new St10bCauseSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).SingleAsync(ct); var persistedAgent = await verify.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, ct); var persistedReceipt = await verify.AgentHeartbeatReceipts.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.HeartbeatId == heartbeatId, ct); var successor = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceLastHeartbeatReceivedAt == persistedReceipt.ReceivedAt && x.ProbeId == source.ProbeId, ct); var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == original.CauseId, ct); var projection = await verify.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == source.ProbeId).Select(x => new St10bProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId, x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId)).SingleAsync(ct); var artifactsAfter = await ReadT4B1ArtifactsAsync(verify, source.ProbeId, ct);
        Assert.Equal(originalSnapshot, originalAfter); Assert.Equal((original.CauseId, source.ProbeId, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityHeartbeatAdvancedReasonCode, fixture.PolicyId, 1, (DateTimeOffset?)null), (disposition.CauseId, disposition.ProbeId, disposition.Outcome, disposition.ReasonCode, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.AppliedAt)); Assert.InRange(disposition.ExpiryCutoffReceivedAt, before, after); Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.CauseId == original.CauseId, ct)); Assert.Equal(0, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.CauseId == successor.CauseId, ct)); Assert.Equal(baselineH1DispositionCount + 1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == source.ProbeId, ct)); Assert.Equal(0, await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().CountAsync(x => x.CauseId == original.CauseId, ct)); Assert.Equal(0, await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().CountAsync(x => x.CauseId == successor.CauseId, ct)); Assert.Equal(fixture.AgentId, persistedAgent.Id); Assert.Equal((fixture.AgentId, heartbeatId), (persistedReceipt.AgentId, persistedReceipt.HeartbeatId)); Assert.NotNull(persistedAgent.LastHeartbeatAt); var laterHeartbeatAt = persistedAgent.LastHeartbeatAt!.Value; Assert.Equal(persistedReceipt.ReceivedAt, laterHeartbeatAt); Assert.Equal(20, persistedAgent.HeartbeatIntervalSeconds); Assert.True(laterHeartbeatAt > oldHeartbeat); Assert.InRange(laterHeartbeatAt, before, after); Assert.Equal(Assert.IsType<string>(h2Payload), persistedReceipt.ResponseJson); Assert.Equal(response.ReceivedAt, persistedReceipt.ReceivedAt); Assert.Equal(laterHeartbeatAt, successor.SourceLastHeartbeatReceivedAt); Assert.Equal(persistedReceipt.ReceivedAt, successor.SourceLastHeartbeatReceivedAt); Assert.Equal(persistedAgent.HeartbeatIntervalSeconds, successor.SourceHeartbeatIntervalSeconds); Assert.Equal(laterHeartbeatAt.AddSeconds(60), successor.DueAt); Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, source.ProbeId, fixture.AgentId, sourceResultId, sourceEventAt, 100L, fixture.GroupId, fixture.PolicyId, 1), (successor.CauseType, successor.SourceDisposition, successor.ProbeId, successor.AuthorityAgentId, successor.SourceResultId, successor.SourceCursorEventAt, successor.SourceConfigurationVersion, successor.SourceAgentGroupId, successor.PolicySnapshotId, successor.PolicyVersion)); Assert.NotEqual(Guid.Empty, successor.CauseId); Assert.NotEqual(original.CauseId, successor.CauseId); Assert.InRange(successor.RequestedAt, before, after); Assert.Equal(projectionBefore, projection); Assert.True(artifactsBefore.ResultDispositions.SequenceEqual(artifactsAfter.ResultDispositions)); Assert.True(artifactsBefore.ResultTransitions.SequenceEqual(artifactsAfter.ResultTransitions)); Assert.True(artifactsBefore.Incidents.SequenceEqual(artifactsAfter.Incidents)); Assert.True(artifactsBefore.Events.SequenceEqual(artifactsAfter.Events)); Assert.True(artifactsBefore.Contexts.SequenceEqual(artifactsAfter.Contexts)); Assert.Equal(1, await verify.AgentHeartbeatReceipts.AsNoTracking().CountAsync(x => x.AgentId == fixture.AgentId && x.HeartbeatId == heartbeatId, ct)); Assert.Equal(baseline.Receipts.Length + 1, await verify.AgentHeartbeatReceipts.AsNoTracking().CountAsync(x => x.AgentId == fixture.AgentId, ct)); Assert.Equal(baseline.Causes.Length + 1, await verify.ProbeHeartbeatExpiryCauses.CountAsync(x => x.ProbeId == source.ProbeId && x.AuthorityAgentId == fixture.AgentId, ct));
    }

    private static async Task<St10bHeartbeatFixture> CreateSt10bHeartbeatFixtureAsync(WebApplicationFactory<Program> factory, HttpClient client, bool projections, CancellationToken ct, int eligibleProjectionCount = int.MaxValue)
    {
        var group = await CreateGroup(client, ct); _ = await SendAdmin<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/24"], group.RowVersion), ct);
        var issued = await Issue(client, group.Id, ct); var enrolled = await Enroll(client, issued, Guid.Parse("70000000-0000-0000-0000-000000000001"), "st10b-agent", ct);
        var groupId = Guid.Parse(group.Id); var agentId = enrolled.AgentId; var probeIds = new[] { Guid.Parse("30000000-0000-0000-0000-000000000003"), Guid.Parse("10000000-0000-0000-0000-000000000001") }; Assert.False(probeIds.SequenceEqual(probeIds.OrderBy(id => id.ToString("D").ToLowerInvariant(), StringComparer.Ordinal))); var now = new DateTimeOffset(2026, 8, 28, 1, 2, 3, 456700, TimeSpan.Zero);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var site = new Site(Guid.Parse("40000000-0000-0000-0000-000000000001"), "T1A", "T1A", "UTC", now); var device = new Device(Guid.Parse("50000000-0000-0000-0000-000000000001"), site.Id, "T1A", "192.0.2.10", null, "Server", null, null, Criticality.Normal, [], now); var policy = new ProbeStatusPolicySnapshot(Guid.Parse("60000000-0000-0000-0000-000000000001"), 1, 1, 1, 500, null, now);
        var configuration = new AgentConfigurationSnapshot(groupId, 100, $$"""{"probes":[{"probeId":"{{probeIds[0]:D}}","intervalSeconds":30},{"probeId":"{{probeIds[1]:D}}","intervalSeconds":30}]}""", new byte[32], now, null); db.AddRange(site, device, policy, configuration);
        var sources = new List<St10bProbeSource>(); foreach (var (probeId, index) in probeIds.Select((id, i) => (id, i))) { var probe = new Probe(probeId, device.Id, groupId, 30, 2000, 3, 500, null, 1, 1); var resultId = Guid.Parse($"80000000-0000-0000-0000-00000000000{index + 1}"); var eventAt = now.AddSeconds(index); var ledger = new ProbeResultLedgerEntry(agentId, resultId, probeId, 100, eventAt.AddSeconds(-1), eventAt, 1, 1, 0m, 1m, 1m, 1m, null, new byte[32], eventAt); db.AddRange(probe, new ProbeStatusPolicyBinding(probeId, 100, groupId, policy.Id), ledger, new ProbeResultProcessingDisposition(agentId, resultId, probeId, eventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, 1, eventAt)); var hasProjection = projections && index < eligibleProjectionCount; if (hasProjection) db.Add(new ProbeStatusProjection(probeId, ProbeStatus.Up, 0, 1, eventAt, eventAt, agentId, resultId)); sources.Add(new(probeId, resultId, eventAt, hasProjection)); }
        await db.SaveChangesAsync(ct); return new(groupId, agentId, enrolled.AgentCredential, policy.Id, sources);
    }

    private static async Task<int> GetBackendPidAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    { await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection); return Assert.IsType<int>(await command.ExecuteScalarAsync(cancellationToken)); }

    private static async Task AcquireProbeAdvisoryLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid probeId, CancellationToken cancellationToken)
    { await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@probeId, 0))", connection, transaction); command.Parameters.AddWithValue("probeId", probeId.ToString("D")); await command.ExecuteNonQueryAsync(cancellationToken); }

    private static async Task LockAgentForShareWithoutFollowUpCommandAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid agentId, Action<NpgsqlCommand> captureCommand, CancellationToken cancellationToken)
    { await using var command = new NpgsqlCommand("SELECT id FROM agents WHERE id = @agentId FOR SHARE", connection, transaction); command.Parameters.AddWithValue("agentId", agentId); captureCommand(command); Assert.Equal(agentId, Assert.IsType<Guid>(await command.ExecuteScalarAsync(cancellationToken))); }

    private static async Task<St10bArtifactSnapshot> ReadT4B1ArtifactsAsync(EePulseDbContext db, Guid probeId, CancellationToken cancellationToken)
    {
        var dispositions = await db.ProbeResultProcessingDispositions.AsNoTracking().Where(x => x.ProbeId == probeId).OrderBy(x => x.AgentId).ThenBy(x => x.ResultId).Select(x => new St10bResultDispositionSnapshot(x.AgentId, x.ResultId, x.ProbeId, x.EventAt, x.Disposition, x.ReasonCode, x.ResolvedPolicySnapshotId, x.ResolvedPolicyVersion, x.DecidedAt)).ToArrayAsync(cancellationToken);
        var transitions = await db.ProbeResultStatusTransitions.AsNoTracking().Where(x => x.ProbeId == probeId).OrderBy(x => x.AgentId).ThenBy(x => x.ResultId).Select(x => new St10bResultTransitionSnapshot(x.AgentId, x.ResultId, x.ProbeId, x.FromStatus, x.ToStatus, x.ReasonCode, x.EventAt, x.ReceivedAt, x.ProcessingDisposition)).ToArrayAsync(cancellationToken);
        var incidents = await db.AvailabilityIncidents.AsNoTracking().Where(x => x.ProbeId == probeId).OrderBy(x => x.Id).Select(x => new St10bIncidentSnapshot(x.Id, x.ProbeId, x.RuleKey, x.Status, x.OpenedAt, x.AcknowledgedAt, x.AcknowledgedBy, x.AcknowledgementComment, x.ResolvedAt, x.ResolvedBy, x.ResolutionNote, x.OccurrenceCount)).ToArrayAsync(cancellationToken);
        var events = await db.IncidentLifecycleEvents.AsNoTracking().Where(x => x.ProbeId == probeId).OrderBy(x => x.EventId).Select(x => new St10bLifecycleEventSnapshot(x.EventId, x.IncidentId, x.ProbeId, x.SourceAgentId, x.SourceResultId, x.SourceFromStatus, x.SourceToStatus, x.SourceReasonCode, x.PolicySnapshotId, x.PolicyVersion, x.LifecycleEventType, x.LifecycleEventKey, x.ProcessingDisposition, x.OccurredAt)).ToArrayAsync(cancellationToken);
        var contexts = await db.NotificationSuppressionContexts.AsNoTracking().Where(x => db.IncidentLifecycleEvents.Any(e => e.EventId == x.EventId && e.ProbeId == probeId)).OrderBy(x => x.EventId).Select(x => new St10bSuppressionContextSnapshot(x.EventId, x.IncidentId, x.LifecycleEventKey, x.PolicyVersion, x.Eligibility, x.ReasonCode, x.EvaluatedAt)).ToArrayAsync(cancellationToken);
        return new(dispositions, transitions, incidents, events, contexts);
    }

    private static async Task<bool> ObserveT4A2TerminalAsync(string name, Task task, bool expectedCancellation, List<Exception> failures)
    {
        try { using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await task.WaitAsync(bounded.Token); return true; }
        catch (OperationCanceledException) when (task.IsCompleted && expectedCancellation) { return true; }
        catch (Exception failure) when (task.IsCompleted) { failures.Add(new InvalidOperationException($"T4A2 cleanup observed {name} complete with an unexpected failure.", failure)); return true; }
        catch (Exception failure) { failures.Add(new InvalidOperationException($"T4A2 cleanup timed out while awaiting {name} to a terminal state.", failure)); return false; }
    }

    private sealed record T4B2BackendIdentity(int Pid, string ApplicationName, string BackendStartedAt, string DatabaseName);

    private static async Task<T4B2BackendIdentity> CaptureT4B2BackendIdentityAsync(NpgsqlConnection observer, string applicationName, Task task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { while (true) { attempts++; await using var command = new NpgsqlCommand("SELECT pid, application_name, backend_start::text, datname FROM pg_stat_activity WHERE application_name = @applicationName AND state <> 'idle' ORDER BY pid", observer); command.Parameters.AddWithValue("applicationName", applicationName); var rows = new List<T4B2BackendIdentity>(); await using var reader = await command.ExecuteReaderAsync(timeout.Token); while (await reader.ReadAsync(timeout.Token)) rows.Add(new T4B2BackendIdentity(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))); last = rows.Count == 0 ? "<none>" : string.Join(" | ", rows.Select(x => $"pid={x.Pid},application={x.ApplicationName},backend_start={x.BackendStartedAt},database={x.DatabaseName}")); if (rows.Count == 1) return rows[0]; if (task.IsCompleted) { await task; throw new Xunit.Sdk.XunitException($"T4B2 operation completed before one backend identity was observable. ApplicationName={applicationName}; backends={last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); } await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token); } }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for T4B2 backend identity. ApplicationName={applicationName}; backends={last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private static async Task<bool> SettleT4B2TaskAsync(string name, Task? task, CancellationTokenSource cancellation, T4B2BackendIdentity? backend, NpgsqlConnection observer, List<Exception> failures)
    {
        if (task is null) return true;
        if (await ObserveT4A2TerminalAsync(name, task, false, failures)) return true;
        try { cancellation.Cancel(); } catch (Exception failure) { failures.Add(new InvalidOperationException($"T4B2 cleanup failed while canceling {name}'s dedicated token.", failure)); }
        if (await ObserveT4A2TerminalAsync($"{name} after dedicated cancellation", task, true, failures)) return true;
        if (backend is null) failures.Add(new InvalidOperationException($"T4B2 cleanup cannot cancel {name}'s backend because its immutable backend identity was not captured.")); else await SignalT4B2BackendAsync($"canceling {name} backend", observer, backend, false, failures);
        if (await ObserveT4A2TerminalAsync($"{name} after pg_cancel_backend", task, true, failures)) return true;
        if (backend is null) failures.Add(new InvalidOperationException($"T4B2 cleanup cannot terminate {name}'s backend because its immutable backend identity was not captured.")); else await SignalT4B2BackendAsync($"terminating {name} backend", observer, backend, true, failures);
        return await ObserveT4A2TerminalAsync($"{name} after pg_terminate_backend", task, true, failures);
    }

    private sealed record T4B2BackendSignalResult(bool PidExists, string? ObservedIdentity, bool SignalAttempted, bool? SignalResult);

    private static async Task SignalT4B2BackendAsync(string stage, NpgsqlConnection observer, T4B2BackendIdentity expected, bool terminate, List<Exception> failures)
    {
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var operation = terminate ? "pg_terminate_backend" : "pg_cancel_backend";
            await using var command = new NpgsqlCommand($"""
                WITH current_backend AS (
                    SELECT pid, application_name, backend_start::text AS backend_start, datname
                    FROM pg_stat_activity
                    WHERE pid = @pid),
                matched_backend AS (
                    SELECT pid
                    FROM current_backend
                    WHERE application_name = @applicationName
                      AND backend_start = @backendStartedAt
                      AND datname = @databaseName),
                signal AS (
                    SELECT {operation}(pid) AS result
                    FROM matched_backend)
                SELECT EXISTS (SELECT 1 FROM current_backend),
                       (SELECT application_name || '|' || backend_start || '|' || datname FROM current_backend),
                       EXISTS (SELECT 1 FROM matched_backend),
                       (SELECT result FROM signal)
                """, observer);
            command.Parameters.AddWithValue("pid", expected.Pid); command.Parameters.AddWithValue("applicationName", expected.ApplicationName); command.Parameters.AddWithValue("backendStartedAt", expected.BackendStartedAt); command.Parameters.AddWithValue("databaseName", expected.DatabaseName);
            await using var reader = await command.ExecuteReaderAsync(token.Token);
            if (!await reader.ReadAsync(token.Token)) throw new Xunit.Sdk.XunitException($"T4B2 cleanup signal returned no diagnostic row. expected pid={expected.Pid}, application={expected.ApplicationName}, backend_start={expected.BackendStartedAt}, database={expected.DatabaseName}.");
            var result = new T4B2BackendSignalResult(reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetBoolean(3));
            if (!result.SignalAttempted) failures.Add(new InvalidOperationException($"T4B2 cleanup did not signal an ambiguous backend while {stage}. expected pid={expected.Pid}, application={expected.ApplicationName}, backend_start={expected.BackendStartedAt}, database={expected.DatabaseName}; pidExists={result.PidExists}; observed={(result.ObservedIdentity ?? "<absent>")}; signalAttempted=false; signalResult=<null>."));
            else if (result.SignalResult != true) failures.Add(new InvalidOperationException($"T4B2 cleanup signal returned false while {stage}. expected pid={expected.Pid}, application={expected.ApplicationName}, backend_start={expected.BackendStartedAt}, database={expected.DatabaseName}; pidExists={result.PidExists}; observed={(result.ObservedIdentity ?? "<absent>")}; signalAttempted=true; signalResult={result.SignalResult}."));
        }
        catch (Exception failure) { failures.Add(new InvalidOperationException($"T4B2 cleanup failed while {stage}.", failure)); }
    }

    private static readonly object T4B2DeferredOwnershipGate = new();
    private static readonly Dictionary<string, T4B2DeferredOwnership> T4B2DeferredOwnerships = [];
    private static readonly Dictionary<string, T4B2DeferredCleanupDiagnosticHolder> T4B2DeferredDiagnostics = [];

    private sealed record T4B2DeferredResource(string Name, Func<Task> DisposeAsync);
    private sealed record T4B2DeferredCleanupOutcome(string Stage, string Outcome, Exception? Exception);

    private sealed class T4B2DeferredCleanupDiagnosticHolder(string operationId)
    {
        private readonly object gate = new();
        private readonly List<T4B2DeferredCleanupOutcome> outcomes = [];
        public string OperationId { get; } = operationId;
        public void Add(string stage, string outcome, Exception? exception = null) { lock (gate) outcomes.Add(new T4B2DeferredCleanupOutcome(stage, outcome, exception)); }
        public IReadOnlyList<T4B2DeferredCleanupOutcome> Snapshot() { lock (gate) return outcomes.ToArray(); }
    }

    private sealed class T4B2DeferredOwnership
    {
        private readonly Task task;
        private readonly CancellationTokenSource cancellation;
        private readonly T4B2BackendIdentity? backend;
        private readonly IReadOnlyList<T4B2DeferredResource> resources;
        private readonly T4B2DeferredCleanupDiagnosticHolder diagnostics;
        private readonly TaskCompletionSource<bool> startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public T4B2DeferredOwnership(string operationId, string operationName, Task task, CancellationTokenSource cancellation, T4B2BackendIdentity? backend, IReadOnlyList<T4B2DeferredResource> resources, T4B2DeferredCleanupDiagnosticHolder diagnostics)
        {
            OperationId = operationId; OperationName = operationName; this.task = task; this.cancellation = cancellation; this.backend = backend; this.resources = resources; this.diagnostics = diagnostics;
        }

        public string OperationId { get; }
        public string OperationName { get; }
        public bool OwnershipTransferred { get; private set; }
        public Task CompletionTask { get; private set; } = Task.CompletedTask;

        public void Start()
        {
            Exception? startupFailure = null;
            lock (T4B2DeferredOwnershipGate)
            {
                try
                {
                    OwnershipTransferred = true;
                    T4B2DeferredOwnerships.Add(OperationId, this);
                    CompletionTask = CompleteAsync();
                    if (!T4B2DeferredOwnerships.TryGetValue(OperationId, out var rooted) || !ReferenceEquals(rooted, this)) throw new InvalidOperationException($"T4B2 deferred ownership registration lost operationId={OperationId} before CompletionTask assignment.");
                }
                catch (Exception exception)
                {
                    startupFailure = exception;
                    if (T4B2DeferredOwnerships.TryGetValue(OperationId, out var rooted) && ReferenceEquals(rooted, this)) T4B2DeferredOwnerships.Remove(OperationId);
                    diagnostics.Add("startup", "failed", exception);
                    CompletionTask = RecoverFromStartupFailureAsync();
                    T4B2DeferredOwnerships[OperationId] = this;
                }
            }
            diagnostics.Add("startup", startupFailure is null ? "started" : "recovered", startupFailure);
            startGate.TrySetResult(true);
        }

        private async Task CompleteAsync()
        {
            await startGate.Task.ConfigureAwait(false);
            await ObserveTaskAndDisposeAsync().ConfigureAwait(false);
        }

        private async Task RecoverFromStartupFailureAsync()
        {
            await startGate.Task.ConfigureAwait(false);
            await ObserveTaskAndDisposeAsync().ConfigureAwait(false);
        }

        private async Task ObserveTaskAndDisposeAsync()
        {
            var failures = new List<Exception>();
            try
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException exception) { diagnostics.Add("task", "canceled", exception); }
                catch (Exception exception)
                {
                    foreach (var inner in exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception]) { var failure = new InvalidOperationException($"T4B2 deferred {OperationName} task fault. operationId={OperationId}.", inner); failures.Add(failure); diagnostics.Add("task", "faulted", failure); }
                }
                if (task.Status == TaskStatus.RanToCompletion) diagnostics.Add("task", "succeeded");
                foreach (var resource in resources)
                {
                    try { await resource.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { var failure = new InvalidOperationException($"T4B2 deferred disposal failed for {resource.Name}. operationId={OperationId}.", exception); failures.Add(failure); diagnostics.Add(resource.Name, "failed", failure); }
                }
            }
            catch (Exception exception) { var failure = new InvalidOperationException($"T4B2 deferred {OperationName} cleanup runner failed unexpectedly. operationId={OperationId}.", exception); failures.Add(failure); diagnostics.Add("runner", "failed", failure); }
            finally
            {
                try { cancellation.Dispose(); }
                catch (Exception exception) { var failure = new InvalidOperationException($"T4B2 deferred disposal failed for {OperationName} cancellation source. operationId={OperationId}.", exception); failures.Add(failure); diagnostics.Add("cancellation", "failed", failure); }
                try
                {
                    lock (T4B2DeferredOwnershipGate)
                    {
                        T4B2DeferredDiagnostics[OperationId] = diagnostics;
                    }
                }
                catch (Exception exception)
                {
                    diagnostics.Add("diagnostic retention", "failed", exception);
                    lock (T4B2DeferredOwnershipGate) T4B2DeferredDiagnostics[OperationId] = diagnostics;
                }
                finally { lock (T4B2DeferredOwnershipGate) T4B2DeferredOwnerships.Remove(OperationId); }
                _ = backend;
            }
        }
    }

    private static void TransferT4B2Ownership(string operationName, Task task, CancellationTokenSource cancellation, T4B2BackendIdentity? backend, IReadOnlyList<T4B2DeferredResource> resources, List<Exception> failures, Exception? primaryFailure)
    {
        var operationId = $"t4b2-deferred-{Guid.NewGuid():N}";
        var hardFailure = new InvalidOperationException($"T4B2 cleanup could not settle {operationName}; ownership transferred to deferred completion. operationId={operationId}.");
        var diagnostics = new T4B2DeferredCleanupDiagnosticHolder(operationId);
        hardFailure.Data[$"T4B2DeferredCompletion:{operationId}"] = diagnostics;
        if (primaryFailure is not null) primaryFailure.Data[$"T4B2DeferredCompletion:{operationId}"] = diagnostics;
        failures.Add(hardFailure);
        lock (T4B2DeferredOwnershipGate) T4B2DeferredDiagnostics[operationId] = diagnostics;
        var ownership = new T4B2DeferredOwnership(operationId, operationName, task, cancellation, backend, resources, diagnostics);
        ownership.Start();
    }

    private static async Task SignalBackendAsync(NpgsqlConnection observer, int backendPid, bool terminate, CancellationToken cancellationToken)
    { await using var command = new NpgsqlCommand(terminate ? "SELECT pg_terminate_backend(@backendPid)" : "SELECT pg_cancel_backend(@backendPid)", observer); command.Parameters.AddWithValue("backendPid", backendPid); if (!Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken))) throw new Xunit.Sdk.XunitException($"T4A2 cleanup could not {(terminate ? "terminate" : "cancel")} exact backend PID {backendPid}."); }

    private static async Task<int> WaitForApplicationBackendAsync(NpgsqlConnection observer, string applicationName, Task task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { while (true) { attempts++; await using var command = new NpgsqlCommand("SELECT pid, state, wait_event_type, wait_event FROM pg_stat_activity WHERE application_name = @applicationName AND state <> 'idle' ORDER BY pid", observer); command.Parameters.AddWithValue("applicationName", applicationName); var rows = new List<(int Pid, string Text)>(); await using var reader = await command.ExecuteReaderAsync(timeout.Token); while (await reader.ReadAsync(timeout.Token)) rows.Add((reader.GetInt32(0), $"pid={reader.GetInt32(0)},state={reader.GetString(1)},wait_event_type={(reader.IsDBNull(2) ? "<null>" : reader.GetString(2))},wait_event={(reader.IsDBNull(3) ? "<null>" : reader.GetString(3))}")); last = rows.Count == 0 ? "<none>" : string.Join(" | ", rows.Select(x => x.Text)); if (rows.Count == 1) return rows[0].Pid; if (task.IsCompleted) { await task; throw new Xunit.Sdk.XunitException($"H2 completed before one backend was observable. ApplicationName={applicationName}; backends={last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); } await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token); } }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for H2 backend. ApplicationName={applicationName}; backends={last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private static async Task WaitForProbeAdvisoryWaitAsync(NpgsqlConnection observer, int waitingPid, int blockerPid, Guid probeId, Task task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; var canonicalProbe = probeId.ToString("D"); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { while (true) { attempts++; await using var command = new NpgsqlCommand("""
            SELECT a.state, a.wait_event_type, a.wait_event,
                   EXISTS (SELECT 1 FROM pg_locks WHERE pid = @waitingPid AND locktype = 'advisory' AND NOT granted AND (lpad(to_hex(classid::bigint), 8, '0') || lpad(to_hex(objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0')),
                   @blockerPid = ANY(pg_blocking_pids(@waitingPid)),
                   COALESCE((SELECT string_agg(locktype || ':' || mode || ':' || granted::text, ', ' ORDER BY locktype, mode, granted) FROM pg_locks WHERE pid = @waitingPid AND locktype IN ('transactionid', 'advisory')), '<none>'),
                   COALESCE(array_to_string(pg_blocking_pids(@waitingPid), ','), '<none>'),
                   hashtextextended(@probeId, 0),
                   COALESCE((SELECT string_agg(format('classid=%s,objid=%s,objsubid=%s,mode=%s,granted=%s,identity=%s', classid::bigint, objid::bigint, objsubid, mode, granted, ((CASE WHEN classid::bigint >= 2147483648 THEN classid::bigint - 4294967296 ELSE classid::bigint END) * 4294967296) + objid::bigint), ' | ' ORDER BY classid, objid, objsubid, mode, granted) FROM pg_locks WHERE pid = @waitingPid AND locktype = 'advisory'), '<none>')
            FROM pg_stat_activity a WHERE a.pid = @waitingPid
            """, observer); command.Parameters.AddWithValue("waitingPid", waitingPid); command.Parameters.AddWithValue("blockerPid", blockerPid); command.Parameters.AddWithValue("probeId", canonicalProbe); await using var reader = await command.ExecuteReaderAsync(timeout.Token); if (await reader.ReadAsync(timeout.Token)) { last = $"state={reader.GetString(0)},wait_event_type={(reader.IsDBNull(1) ? "<null>" : reader.GetString(1))},wait_event={(reader.IsDBNull(2) ? "<null>" : reader.GetString(2))},locks={reader.GetString(5)},blockingPids={reader.GetString(6)},expectedAdvisoryIdentity={reader.GetInt64(7)},observedAdvisoryLocks={reader.GetString(8)}"; if (string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), "Lock", StringComparison.Ordinal) && reader.GetBoolean(3) && reader.GetBoolean(4)) return; } else last = "<missing>"; if (task.IsCompleted) { await task; throw new Xunit.Sdk.XunitException($"H2 did not wait on the exact Probe advisory lock. waiterPid={waitingPid}; blockerPid={blockerPid}; probeId={canonicalProbe}; {last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); } await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token); } }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for H2 Probe advisory evidence. waiterPid={waitingPid}; blockerPid={blockerPid}; probeId={canonicalProbe}; {last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private static async Task WaitForAgentShareWaitAsync(NpgsqlConnection observer, int waitingPid, int blockerPid, int excludedBlockerPid, Task task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { while (true) { attempts++; await using var command = new NpgsqlCommand("""
            SELECT a.state, a.wait_event_type, a.wait_event,
                   EXISTS (SELECT 1 FROM pg_locks WHERE pid = @waitingPid AND locktype = 'transactionid' AND NOT granted),
                   @blockerPid = ANY(pg_blocking_pids(@waitingPid)),
                   NOT (@excludedBlockerPid = ANY(pg_blocking_pids(@waitingPid))),
                   NOT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @waitingPid AND locktype = 'advisory'),
                   COALESCE((SELECT string_agg(locktype || ':' || mode || ':' || granted::text, ', ' ORDER BY locktype, mode, granted) FROM pg_locks WHERE pid = @waitingPid AND locktype IN ('transactionid', 'advisory')), '<none>'),
                   COALESCE(array_to_string(pg_blocking_pids(@waitingPid), ','), '<none>'),
                   COALESCE((SELECT string_agg(format('classid=%s,objid=%s,objsubid=%s,mode=%s,granted=%s,identity=%s', classid::bigint, objid::bigint, objsubid, mode, granted, ((CASE WHEN classid::bigint >= 2147483648 THEN classid::bigint - 4294967296 ELSE classid::bigint END) * 4294967296) + objid::bigint), ' | ' ORDER BY classid, objid, objsubid, mode, granted) FROM pg_locks WHERE pid = @waitingPid AND locktype = 'advisory'), '<none>')
            FROM pg_stat_activity a WHERE a.pid = @waitingPid
            """, observer); command.Parameters.AddWithValue("waitingPid", waitingPid); command.Parameters.AddWithValue("blockerPid", blockerPid); command.Parameters.AddWithValue("excludedBlockerPid", excludedBlockerPid); await using var reader = await command.ExecuteReaderAsync(timeout.Token); if (await reader.ReadAsync(timeout.Token)) { last = $"state={reader.GetString(0)},wait_event_type={(reader.IsDBNull(1) ? "<null>" : reader.GetString(1))},wait_event={(reader.IsDBNull(2) ? "<null>" : reader.GetString(2))},locks={reader.GetString(7)},blockingPids={reader.GetString(8)},observedAdvisoryLocks={reader.GetString(9)}"; if (string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), "Lock", StringComparison.Ordinal) && reader.GetBoolean(3) && reader.GetBoolean(4) && reader.GetBoolean(5) && reader.GetBoolean(6)) return; } else last = "<missing>"; if (task.IsCompleted) { await task; throw new Xunit.Sdk.XunitException($"Agent FOR SHARE did not wait on H2 without an advisory request. waiterPid={waitingPid}; blockerPid={blockerPid}; excludedBlockerPid={excludedBlockerPid}; {last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); } await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token); } }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for Agent FOR SHARE evidence. waiterPid={waitingPid}; blockerPid={blockerPid}; excludedBlockerPid={excludedBlockerPid}; {last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private static async Task<DateTimeOffset> ReadPostgresClockAsync(string connectionString, CancellationToken ct) { await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); await using var command = new NpgsqlCommand("SELECT date_trunc('microseconds', clock_timestamp())", connection); return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(ct))!); }
    private static async Task<DateTimeOffset> ReadPostgresClockAfterAsync(string connectionString, DateTimeOffset value, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew(); var attempts = 0; var last = value;
        try
        {
            while (true)
            {
                last = await ReadPostgresClockAsync(connectionString, timeout.Token); attempts++;
                if (last > value) return last;
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"PostgreSQL clock did not advance beyond {value:O}; last observed {last:O}; attempts={attempts}; timeout={stopwatch.Elapsed}.");
        }
    }
    private static DateTimeOffset NormalizeMicroseconds(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    private const string St10bFailureSignature = "st10b-h2-final-flush-failure";
    private static async Task<St10bFailureTrigger> InstallSt10bFailureTriggerAsync(string connectionString, Guid agentId, CancellationToken ct)
    { var suffix = Guid.NewGuid().ToString("N"); var resource = new St10bFailureTrigger("st10b_h2_fn_" + suffix, "st10b_h2_tr_" + suffix); if (!Guid.TryParseExact(agentId.ToString("D"), "D", out _)) throw new InvalidOperationException("Invalid test Agent identifier."); var quote = new NpgsqlCommandBuilder(); Exception? primary = null; try { await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); await using (var function = new NpgsqlCommand($"CREATE FUNCTION {quote.QuoteIdentifier(resource.FunctionName)}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.authority_agent_id = '{agentId:D}'::uuid THEN RAISE EXCEPTION '{St10bFailureSignature}'; END IF; RETURN NEW; END; $$;", connection)) await function.ExecuteNonQueryAsync(ct); await using (var trigger = new NpgsqlCommand($"CREATE TRIGGER {quote.QuoteIdentifier(resource.TriggerName)} BEFORE INSERT ON \"public\".\"probe_heartbeat_expiry_causes\" FOR EACH ROW EXECUTE FUNCTION {quote.QuoteIdentifier(resource.FunctionName)}();", connection)) await trigger.ExecuteNonQueryAsync(ct); return resource; } catch (Exception exception) { primary = exception; throw; } finally { if (primary is not null) { try { await DropSt10bFailureTriggerAsync(connectionString, resource, CancellationToken.None); } catch (Exception cleanupFailure) { primary.Data["st10bCleanupFailure"] = cleanupFailure; } } } }
    private static async Task DropSt10bFailureTriggerAsync(string connectionString, St10bFailureTrigger resource, CancellationToken ct)
    { var quote = new NpgsqlCommandBuilder(); Exception? failure = null; try { using var triggerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(triggerTimeout.Token); await using var command = new NpgsqlCommand($"DROP TRIGGER IF EXISTS {quote.QuoteIdentifier(resource.TriggerName)} ON \"public\".\"probe_heartbeat_expiry_causes\"", connection); await command.ExecuteNonQueryAsync(triggerTimeout.Token); } catch (Exception exception) { failure = exception; } try { using var functionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(functionTimeout.Token); await using var command = new NpgsqlCommand($"DROP FUNCTION IF EXISTS {quote.QuoteIdentifier(resource.FunctionName)}()", connection); await command.ExecuteNonQueryAsync(functionTimeout.Token); } catch (Exception exception) { if (failure is null) failure = exception; else failure.Data["st10bFunctionCleanupFailure"] = exception; } if (failure is not null) throw failure; }
    private static async Task<St10bHeartbeatSnapshot> ReadSt10bHeartbeatSnapshotAsync(WebApplicationFactory<Program> factory, Guid agentId, CancellationToken ct)
    { await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>(); var agent = await db.Agents.AsNoTracking().Where(x => x.Id == agentId).Select(x => new St10bAgentSnapshot(x.Id, x.AgentVersion, x.MachineName, x.SelfHealth, x.Status, x.QueueDepth, x.LastHeartbeatAt, x.LastReportedAt, x.HeartbeatIntervalSeconds, x.ClockSkewSuspected, x.RowVersion)).SingleAsync(ct); var receipts = await db.AgentHeartbeatReceipts.AsNoTracking().Where(x => x.AgentId == agentId).OrderBy(x => x.HeartbeatId).Select(x => new St10bReceiptSnapshot(x.AgentId, x.HeartbeatId, x.ReceivedAt, x.ResponseJson)).ToArrayAsync(ct); var causes = await db.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.AuthorityAgentId == agentId).OrderBy(x => x.ProbeId).ThenBy(x => x.SourceLastHeartbeatReceivedAt).Select(x => new St10bCauseSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).ToArrayAsync(ct); return new(agent, receipts, causes); }
    private sealed record St10bProbeSource(Guid ProbeId, Guid ResultId, DateTimeOffset EventAt, bool HasProjection);
    private sealed record St10bHeartbeatFixture(Guid GroupId, Guid AgentId, string Credential, Guid PolicyId, IReadOnlyList<St10bProbeSource> Probes);
    private sealed record St10bAgentSnapshot(Guid Id, string Version, string Machine, AgentSelfHealth Health, AgentStatus Status, long QueueDepth, DateTimeOffset? LastHeartbeatAt, DateTimeOffset? LastReportedAt, int IntervalSeconds, bool ClockSkewSuspected, long RowVersion);
    private sealed record St10bReceiptSnapshot(Guid AgentId, Guid HeartbeatId, DateTimeOffset ReceivedAt, string ResponseJson);
    private sealed record St10bCauseSnapshot(Guid CauseId, Guid ProbeId, ProbeHeartbeatExpiryCauseType CauseType, Guid AuthorityAgentId, Guid SourceResultId, DateTimeOffset SourceCursorEventAt, DateTimeOffset SourceLastHeartbeatAt, int SourceHeartbeatIntervalSeconds, long SourceConfigurationVersion, Guid SourceAgentGroupId, ProbeResultProcessingDispositionKind SourceDisposition, Guid PolicySnapshotId, int PolicyVersion, DateTimeOffset DueAt, DateTimeOffset RequestedAt);
    private sealed record St10bProjectionSnapshot(Guid ProbeId, ProbeStatus UnderlyingStatus, ProbeStatus VisibleStatus, int ConsecutiveFailureCount, int ConsecutiveSuccessCount, long StateVersion, Guid? WatermarkAgentId, Guid? WatermarkResultId, DateTimeOffset? WatermarkEventAt, DateTimeOffset? LastFreshEventAt, Guid? OpenIncidentId);
    private sealed record St10bResultDispositionSnapshot(Guid AgentId, Guid ResultId, Guid ProbeId, DateTimeOffset EventAt, ProbeResultProcessingDispositionKind Disposition, string ReasonCode, Guid? ResolvedPolicySnapshotId, int? ResolvedPolicyVersion, DateTimeOffset DecidedAt);
    private sealed record St10bResultTransitionSnapshot(Guid AgentId, Guid ResultId, Guid ProbeId, ProbeStatus FromStatus, ProbeStatus ToStatus, string ReasonCode, DateTimeOffset EventAt, DateTimeOffset ReceivedAt, ProbeResultProcessingDispositionKind ProcessingDisposition);
    private sealed record St10bIncidentSnapshot(Guid Id, Guid ProbeId, string RuleKey, AvailabilityIncidentStatus Status, DateTimeOffset OpenedAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, string? AcknowledgementComment, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? ResolutionNote, int OccurrenceCount);
    private sealed record St10bLifecycleEventSnapshot(Guid EventId, Guid IncidentId, Guid ProbeId, Guid SourceAgentId, Guid SourceResultId, ProbeStatus SourceFromStatus, ProbeStatus SourceToStatus, string SourceReasonCode, Guid PolicySnapshotId, int PolicyVersion, IncidentLifecycleEventType LifecycleEventType, string LifecycleEventKey, ProbeResultProcessingDispositionKind ProcessingDisposition, DateTimeOffset OccurredAt);
    private sealed record St10bSuppressionContextSnapshot(Guid EventId, Guid IncidentId, string LifecycleEventKey, int PolicyVersion, NotificationSuppressionEligibility Eligibility, string ReasonCode, DateTimeOffset EvaluatedAt);
    private sealed record St10bArtifactSnapshot(St10bResultDispositionSnapshot[] ResultDispositions, St10bResultTransitionSnapshot[] ResultTransitions, St10bIncidentSnapshot[] Incidents, St10bLifecycleEventSnapshot[] Events, St10bSuppressionContextSnapshot[] Contexts);
    private sealed record St10bHeartbeatSnapshot(St10bAgentSnapshot Agent, St10bReceiptSnapshot[] Receipts, St10bCauseSnapshot[] Causes);
    private sealed record St10bFailureTrigger(string FunctionName, string TriggerName);

    private static AgentHeartbeatRequest Heartbeat(Guid id) => new(1, id, "1.2.3", "probe-a", 1, 0, "Healthy", DateTimeOffset.UtcNow);
    private static System.Text.Json.JsonSerializerOptions CreateAgentJson() { var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web); AgentJsonContract.AddConverters(options); return options; }
    private static JsonContent AgentContent(object body) => JsonContent.Create(body, body.GetType(), new MediaTypeHeaderValue("application/json"), AgentJson);
    private static Task<HttpResponseMessage> PostAgentJson(HttpClient client, string path, object body, CancellationToken ct) => client.PostAsync(path, AgentContent(body), ct);
    private static async Task<AgentGroupResponse> CreateGroup(HttpClient client, CancellationToken ct) => await SendAdmin<AgentGroupResponse>(client, HttpMethod.Post, "/api/v1/agent-groups", new CreateAgentGroupRequest($"Agents {Guid.NewGuid():N}", null), ct);
    private static async Task<CreateAgentEnrollmentTokenResponse> Issue(HttpClient client, string groupId, CancellationToken ct) => await SendAdmin<CreateAgentEnrollmentTokenResponse>(client, HttpMethod.Post, "/api/v1/agent-enrollment-tokens", new CreateAgentEnrollmentTokenRequest(1, Guid.Parse(groupId), "integration", null, ["192.0.2.0/24", "198.51.100.0/24"]), ct);
    private static async Task<AgentEnrollmentResponse> Enroll(HttpClient client, CreateAgentEnrollmentTokenResponse issued, Guid instance, string machine, CancellationToken ct) { var response = await PostAgentJson(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, issued.EnrollmentToken, instance, machine, "1.2.3", issued.AllowedNetworks, DateTimeOffset.UtcNow), ct); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await response.Content.ReadFromJsonAsync<AgentEnrollmentResponse>(ct))!; }
    private static async Task<T> SendAdmin<T>(HttpClient client, HttpMethod method, string path, object? body, CancellationToken ct) { using var request = AdminRequest(method, path, body is null ? null : AgentContent(body)); var response = await client.SendAsync(request, ct); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct)); return (await response.Content.ReadFromJsonAsync<T>(ct))!; }
    private static HttpRequestMessage AdminRequest(HttpMethod method, string path, HttpContent? body) { var request = new HttpRequestMessage(method, path) { Content = body }; request.Headers.Add("X-EE-Pulse-Role", "Administrator"); request.Headers.Add("X-EE-Pulse-Actor", ActorId.ToString()); return request; }
    private static HttpRequestMessage AgentRequest(HttpMethod method, string path, string credential, HttpContent? body = null) { var request = new HttpRequestMessage(method, path) { Content = body }; request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential); return request; }
    private static async Task<T> SendAgent<T>(HttpClient client, HttpMethod method, string path, string credential, object body, HttpStatusCode status, CancellationToken ct) { using var request = AgentRequest(method, path, credential, AgentContent(body)); var response = await client.SendAsync(request, ct); Assert.Equal(status, response.StatusCode); return (await response.Content.ReadFromJsonAsync<T>(ct))!; }
    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    { protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(content).AsTask(); protected override bool TryComputeLength(out long length) { length = 0; return false; } }
}
