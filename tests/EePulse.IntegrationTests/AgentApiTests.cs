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
