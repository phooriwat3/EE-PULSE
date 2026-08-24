using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EePulse.Api.Agents;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Inventory;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EePulse.IntegrationTests;

public sealed class ProbeResultIngestionApiTests
{
    private static readonly Guid ActorId = Guid.Parse("6a7f78d4-679d-4ed2-9aea-1c395a439d30");
    private static readonly JsonSerializerOptions AgentJson = CreateAgentJson();

    [Fact]
    public async Task ResultIngestionIsAuthenticatedValidatedIdempotentAndSanitized()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        using var client = factory.CreateClient();
        var enrolled = await EnrollConfiguredAgent(client, ct);
        var result = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);

        var accepted = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result]), HttpStatusCode.OK, ct);
        Assert.Equal([result.ResultId], accepted.AcceptedResultIds);
        Assert.Empty(accepted.Rejections);
        await AssertLedgerCount(factory, 1, ct);

        var replay = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result]), HttpStatusCode.OK, ct);
        Assert.Equal([result.ResultId], replay.AcceptedResultIds);
        await AssertLedgerCount(factory, 1, ct);

        var conflictPayload = JsonSerializer.Serialize(new
        {
            batchId = Guid.NewGuid(),
            results = new[]
            {
                new
                {
                    result.ResultSchemaVersion, result.ResultId, result.AgentId, result.ProbeId, result.ConfigurationVersion, result.StartedAt, result.EndedAt,
                    result.AttemptCount, result.SuccessfulAttemptCount, packetLossRatio = 1m, result.MinRttMilliseconds, result.AverageRttMilliseconds, result.MaxRttMilliseconds, result.ErrorCategory
                }
            }
        }, AgentJson);
        ProbeResultIngestionBatchResponse conflict;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/result-batches") { Content = new StringContent(conflictPayload, Encoding.UTF8, "application/json") })
        { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Credential); var response = await client.SendAsync(request, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode); conflict = (await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!; }
        Assert.Empty(conflict.AcceptedResultIds);
        Assert.Equal("result-identity-conflict", Assert.Single(conflict.Rejections).Code);
        await AssertLedgerCount(factory, 1, ct);
        await AssertSafeConflictAudit(factory, enrolled.AgentId, result.ResultId, enrolled.Credential, ct);

        var subMicrosecondConflict = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result with { StartedAt = result.StartedAt.AddTicks(1) }]), HttpStatusCode.OK, ct);
        Assert.Empty(subMicrosecondConflict.AcceptedResultIds);
        Assert.Equal("result-identity-conflict", Assert.Single(subMicrosecondConflict.Rejections).Code);
        await AssertLedgerCount(factory, 1, ct);

        var sameBatchReplay = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);
        var sameBatchAcknowledgement = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [sameBatchReplay, sameBatchReplay]), HttpStatusCode.OK, ct);
        Assert.Equal([sameBatchReplay.ResultId], sameBatchAcknowledgement.AcceptedResultIds);
        Assert.Empty(sameBatchAcknowledgement.Rejections);
        await AssertLedgerCount(factory, 2, ct);

        var sameBatchConflict = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);
        var sameBatchConflictResponse = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [sameBatchConflict, sameBatchConflict with { PacketLossRatio = 1m }]), HttpStatusCode.OK, ct);
        Assert.Empty(sameBatchConflictResponse.AcceptedResultIds);
        Assert.Equal("result-identity-conflict", Assert.Single(sameBatchConflictResponse.Rejections).Code);
        await AssertLedgerCount(factory, 2, ct);

        const decimal maximumRtt = 999999999999.999999m;
        var maximumRttResponse = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion) with { MinRttMilliseconds = maximumRtt, AverageRttMilliseconds = maximumRtt, MaxRttMilliseconds = maximumRtt }]), HttpStatusCode.OK, ct);
        Assert.Single(maximumRttResponse.AcceptedResultIds);
        await AssertLedgerCount(factory, 3, ct);
        var overflowRtt = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion) with { MinRttMilliseconds = 1_000_000_000_000m, AverageRttMilliseconds = 1_000_000_000_000m, MaxRttMilliseconds = 1_000_000_000_000m }]), HttpStatusCode.OK, ct);
        Assert.Empty(overflowRtt.AcceptedResultIds);
        Assert.Equal("result-invalid", Assert.Single(overflowRtt.Rejections).Code);
        var overScaleRtt = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion) with { MinRttMilliseconds = 0.0000001m, AverageRttMilliseconds = 0.0000001m, MaxRttMilliseconds = 0.0000001m }]), HttpStatusCode.OK, ct);
        Assert.Empty(overScaleRtt.AcceptedResultIds);
        Assert.Equal("result-invalid", Assert.Single(overScaleRtt.Rejections).Code);
        await AssertLedgerCount(factory, 3, ct);

        var concurrent = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [concurrent]), HttpStatusCode.OK, ct)));
        Assert.All(responses, response => Assert.Equal([concurrent.ResultId], response.AcceptedResultIds));
        await AssertLedgerCount(factory, 4, ct);

        using (var wrongRoute = Request($"/api/v1/agents/{Guid.NewGuid()}/result-batches", enrolled.Credential, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result])))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongRoute, ct)).StatusCode);
        using (var wrongBody = Request($"/api/v1/agents/{enrolled.AgentId}/result-batches", enrolled.Credential, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result with { ResultId = Guid.NewGuid(), AgentId = Guid.NewGuid() }])))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongBody, ct)).StatusCode);

        var unsupported = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result with { ResultId = Guid.NewGuid(), ResultSchemaVersion = 99 }]), HttpStatusCode.OK, ct);
        Assert.Equal(AgentProblemCodes.SchemaUnsupported, Assert.Single(unsupported.Rejections).Code);
        var invalid = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result with { ResultId = Guid.NewGuid(), AttemptCount = 0 }]), HttpStatusCode.OK, ct);
        Assert.Empty(invalid.AcceptedResultIds);
        Assert.Equal("result-invalid", Assert.Single(invalid.Rejections).Code);
        await AssertLedgerCount(factory, 4, ct);

        var nullBatch = JsonSerializer.Serialize(new { batchId = Guid.NewGuid(), results = new object?[] { null } }, AgentJson);
        using (var nullRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/result-batches") { Content = new StringContent(nullBatch, Encoding.UTF8, "application/json") })
        { nullRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Credential); var response = await client.SendAsync(nullRequest, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct); Assert.Empty(body!.AcceptedResultIds); Assert.Equal("result-invalid", Assert.Single(body.Rejections).Code); }
        await AssertLedgerCount(factory, 4, ct);

        const string canary = "secret-result-canary";
        var nonUtc = JsonSerializer.Serialize(new
        {
            batchId = Guid.NewGuid(),
            results = new[] { new { resultSchemaVersion = 1, resultId = Guid.NewGuid(), agentId = enrolled.AgentId, probeId = enrolled.ProbeId, configurationVersion = enrolled.ConfigurationVersion, startedAt = "2026-08-23T13:00:00+00:00", endedAt = "2026-08-23T13:00:01Z", attemptCount = 1, successfulAttemptCount = 1, packetLossRatio = 0, errorCategory = canary } }
        }, AgentJson);
        using (var nonUtcRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/result-batches") { Content = new StringContent(nonUtc, Encoding.UTF8, "application/json") })
        { nonUtcRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Credential); var response = await client.SendAsync(nonUtcRequest, ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); var body = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain(canary, body, StringComparison.Ordinal); Assert.Equal(AgentProblemCodes.TimestampNotUtc, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()); }
        using (var badCredential = Request($"/api/v1/agents/{enrolled.AgentId}/result-batches", "malformed-secret-canary", new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [])))
        { var response = await client.SendAsync(badCredential, ct); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.DoesNotContain("malformed-secret-canary", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal); }
    }

    private static async Task<(Guid AgentId, Guid ProbeId, long ConfigurationVersion, string Credential)> EnrollConfiguredAgent(HttpClient client, CancellationToken ct)
    {
        var group = await Admin<AgentGroupResponse>(client, HttpMethod.Post, "/api/v1/agent-groups", new CreateAgentGroupRequest($"ingestion-{Guid.NewGuid():N}", null), ct);
        _ = await Admin<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/24"], group.RowVersion), ct);
        var site = await Admin<SiteResponse>(client, HttpMethod.Post, "/api/v1/sites", new CreateSiteRequest("ING" + Guid.NewGuid().ToString("N")[..6], "Ingestion", "UTC"), ct);
        var device = await Admin<DeviceResponse>(client, HttpMethod.Post, "/api/v1/devices", new CreateDeviceRequest(site.Id, "target", "192.0.2.10", null, "server", null, null, "Normal", []), ct);
        var probe = await Admin<ProbeResponse>(client, HttpMethod.Post, "/api/v1/probes", new CreateProbeRequest(device.Id, group.Id, 20, 1000, 1, null, null, 1, 1), ct);
        var token = await Admin<CreateAgentEnrollmentTokenResponse>(client, HttpMethod.Post, "/api/v1/agent-enrollment-tokens", new CreateAgentEnrollmentTokenRequest(1, Guid.Parse(group.Id), "ingestion", null, ["192.0.2.0/24"]), ct);
        var enrollment = await Post<AgentEnrollmentResponse>(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, token.EnrollmentToken, Guid.NewGuid(), "ingestion-agent", "1.2.3", token.AllowedNetworks, DateTimeOffset.UtcNow), ct);
        var configuration = await Get<AgentConfigurationResponse>(client, $"/api/v1/agents/{enrollment.AgentId}/configuration", enrollment.AgentCredential, ct);
        _ = await SendAck(client, enrollment, configuration.ConfigurationVersion, ct);
        return (enrollment.AgentId, Guid.Parse(probe.Id), configuration.ConfigurationVersion, enrollment.AgentCredential);
    }

    private static ProbeResultIngestionEnvelope Result(Guid agentId, Guid probeId, long version) => new(1, Guid.NewGuid(), agentId, probeId, version, new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 23, 13, 0, 1, TimeSpan.Zero), 1, 1, 0m, 1m, 1m, 1m, null);
    private static async Task<ProbeResultIngestionBatchResponse> Send(HttpClient client, (Guid AgentId, Guid ProbeId, long ConfigurationVersion, string Credential) agent, ProbeResultIngestionBatchRequest request, HttpStatusCode expected, CancellationToken ct) { using var message = Request($"/api/v1/agents/{agent.AgentId}/result-batches", agent.Credential, request); var response = await client.SendAsync(message, ct); Assert.Equal(expected, response.StatusCode); return (await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!; }
    private static HttpRequestMessage Request(string path, string credential, object body) { var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, body.GetType(), new MediaTypeHeaderValue("application/json"), AgentJson) }; request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential); return request; }
    private static async Task<T> Admin<T>(HttpClient client, HttpMethod method, string path, object body, CancellationToken ct) { using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, body.GetType(), new MediaTypeHeaderValue("application/json"), AgentJson) }; request.Headers.Add("X-EE-Pulse-Role", "Administrator"); request.Headers.Add("X-EE-Pulse-Actor", ActorId.ToString()); var response = await client.SendAsync(request, ct); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct)); return (await response.Content.ReadFromJsonAsync<T>(ct))!; }
    private static async Task<T> Post<T>(HttpClient client, string path, object body, CancellationToken ct) { var response = await client.PostAsync(path, JsonContent.Create(body, body.GetType(), new MediaTypeHeaderValue("application/json"), AgentJson), ct); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct)); return (await response.Content.ReadFromJsonAsync<T>(AgentJson, ct))!; }
    private static async Task<T> Get<T>(HttpClient client, string path, string credential, CancellationToken ct) { using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential); var response = await client.SendAsync(request, ct); Assert.True(response.IsSuccessStatusCode); return (await response.Content.ReadFromJsonAsync<T>(AgentJson, ct))!; }
    private static async Task<AgentConfigurationAcknowledgementResponse> SendAck(HttpClient client, AgentEnrollmentResponse agent, long version, CancellationToken ct) { using var request = Request($"/api/v1/agents/{agent.AgentId}/configuration/acknowledgements", agent.AgentCredential, new AgentConfigurationAcknowledgementRequest(1, Guid.NewGuid(), version, "Applied", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow)); var response = await client.SendAsync(request, ct); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct)); return (await response.Content.ReadFromJsonAsync<AgentConfigurationAcknowledgementResponse>(AgentJson, ct))!; }
    private static async Task AssertLedgerCount(WebApplicationFactory<Program> factory, int expected, CancellationToken ct) { await using var scope = factory.Services.CreateAsyncScope(); Assert.Equal(expected, await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().ProbeResultLedgerEntries.CountAsync(ct)); }
    private static async Task AssertSafeConflictAudit(WebApplicationFactory<Program> factory, Guid agentId, Guid resultId, string credential, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = Assert.Single(await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().AuditEvents.Where(x => x.Action == "agent.result.identity-conflict" && x.EntityId == agentId).ToListAsync(ct));
        Assert.Null(audit.ActorId); Assert.Equal("Agent", audit.EntityType); Assert.Null(audit.BeforeJson); Assert.Null(audit.SourceIp); Assert.False(string.IsNullOrWhiteSpace(audit.CorrelationId));
        Assert.DoesNotContain(credential, audit.AfterJson, StringComparison.Ordinal); Assert.DoesNotContain("packetLossRatio", audit.AfterJson, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal(["agentId", "reasonCode", "resultId"], metadata.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
        Assert.Equal(agentId, metadata.RootElement.GetProperty("agentId").GetGuid()); Assert.Equal(resultId, metadata.RootElement.GetProperty("resultId").GetGuid()); Assert.Equal("immutable-payload-digest-mismatch", metadata.RootElement.GetProperty("reasonCode").GetString());
    }
    private static JsonSerializerOptions CreateAgentJson() { var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); AgentJsonContract.AddConverters(options); return options; }
}
