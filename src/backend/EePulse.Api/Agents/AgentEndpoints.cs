using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EePulse.Api.Authorization;
using EePulse.Application.Agents;
using EePulse.Application.Time;
using EePulse.Contracts.Agents;
using EePulse.Domain.Agents;
using EePulse.Domain.Auditing;
using EePulse.Domain.Common;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EePulse.Api.Agents;

public static partial class AgentEndpoints
{
    private const string TokenDomain = "EE-Pulse-Agent-Enrollment-v1";
    private const string CredentialDomain = "EE-Pulse-Agent-Credential-v1";
    private static readonly JsonSerializerOptions CanonicalJson = CreateCanonicalJson();

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");
        Problems(api.MapPost("/agent-enrollment-tokens", IssueToken), 400, 401, 403, 404, 409, 429, 503).RequireAuthorization(InventoryAuthorization.AgentAdminPolicy).Produces<CreateAgentEnrollmentTokenResponse>(201);
        Problems(api.MapDelete("/agent-enrollment-tokens/{tokenId:guid}", RevokeToken), 400, 401, 403, 429, 503).RequireAuthorization(InventoryAuthorization.AgentAdminPolicy).Produces(204);
        Problems(api.MapPost("/agents/enroll", Enroll), 400, 401, 403, 409, 410, 426, 429, 503).AllowAnonymous().DisableAntiforgery().Produces<AgentEnrollmentResponse>(201);
        Problems(api.MapGet("/agents", ListAgents), 400, 401, 403, 503).RequireAuthorization(InventoryAuthorization.AgentReadPolicy).Produces<PagedAgentResponse>();
        Problems(api.MapGet("/agents/{agentId:guid}", GetAgent), 400, 401, 403, 404, 503).RequireAuthorization(InventoryAuthorization.AgentReadPolicy).Produces<AgentResponse>();
        Problems(api.MapPut("/agent-groups/{agentGroupId:guid}/allowed-networks", UpdateGroupNetworks), 400, 401, 403, 404, 409, 429, 503).RequireAuthorization(InventoryAuthorization.AgentAdminPolicy).Produces<AgentNetworkPolicyResponse>();
        Problems(api.MapPut("/agents/{agentId:guid}/allowed-networks", UpdateAgentNetworks), 400, 401, 403, 404, 409, 429, 503).RequireAuthorization(InventoryAuthorization.AgentAdminPolicy).Produces<AgentNetworkPolicyResponse>();
        Problems(api.MapPost("/agent-groups/{agentGroupId:guid}/configuration/rollback", Rollback), 400, 401, 403, 404, 409, 410, 429, 503).RequireAuthorization(InventoryAuthorization.AgentAdminPolicy).Produces<AgentConfigurationPublicationResponse>(201);
        Problems(api.MapPost("/agents/{agentId:guid}/revoke", RevokeAgent), 400, 401, 403, 404, 409, 429, 503).RequireAuthorization(InventoryAuthorization.AgentAdminPolicy).Produces<AgentResponse>();
        Problems(MapAgentOperation(api.MapPost("/agents/{agentId:guid}/heartbeat", Heartbeat)), 400, 401, 403, 404, 410, 426, 429, 503).Produces<AgentHeartbeatResponse>();
        Problems(MapAgentOperation(api.MapGet("/agents/{agentId:guid}/configuration", GetConfiguration)), 401, 403, 404, 409, 410, 503).Produces<AgentConfigurationResponse>().Produces(304);
        Problems(MapAgentOperation(api.MapPost("/agents/{agentId:guid}/configuration/acknowledgements", Acknowledge)), 400, 401, 403, 404, 409, 410, 429, 503).Produces<AgentConfigurationAcknowledgementResponse>();
        Problems(MapAgentOperation(api.MapPost("/agents/{agentId:guid}/credentials/rotate", Rotate)), 400, 401, 403, 404, 410, 429, 503).Produces<RotateAgentCredentialResponse>(201);
        Problems(MapAgentOperation(api.MapPost("/agents/{agentId:guid}/result-batches", IngestResults)), 400, 401, 403, 409, 410, 413, 429, 503).Produces<ProbeResultIngestionBatchResponse>();
        return app;
    }
    private static RouteHandlerBuilder MapAgentOperation(RouteHandlerBuilder route) => route.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AgentContract.CredentialAuthenticationScheme });
    private static RouteHandlerBuilder Problems(RouteHandlerBuilder route, params int[] statuses) { foreach (var status in statuses) route.ProducesProblem(status); return route; }

    private static async Task<IResult> IssueToken(CreateAgentEnrollmentTokenRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, IWebHostEnvironment environment, CancellationToken ct)
    {
        try
        {
            ValidateSchema(request.SchemaVersion); if (request.AgentGroupId == Guid.Empty || request.ExpiresInSeconds is < 60 or > 86400 || string.IsNullOrWhiteSpace(request.Label)) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Enrollment token request values are outside allowed bounds."); var group = await db.AgentGroups.SingleOrDefaultAsync(x => x.Id == request.AgentGroupId, ct); if (group is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent group not found."); if (!group.Enabled) return Problem(http, 409, AgentProblemCodes.AgentGroupDisabled, "Agent group is disabled.");
            var networks = Ipv4NetworkPolicy.Normalize(request.AllowedNetworks, environment.IsDevelopment()); var groupNetworks = await db.AgentGroupAllowedNetworks.Where(x => x.AgentGroupId == group.Id).Select(x => x.Network).ToListAsync(ct); if (groupNetworks.Count == 0 || !await db.AgentConfigurationSnapshots.AnyAsync(x => x.AgentGroupId == group.Id && x.Version == group.ConfigurationVersion, ct)) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Agent group has no published network policy."); if (!Ipv4NetworkPolicy.IsNarrowerOrEqual(groupNetworks, networks)) return Problem(http, 403, AgentProblemCodes.NetworkPolicyMismatch, "Enrollment ceiling is narrower than group policy.");
            var generated = AgentSecret.Create(TokenDomain); var now = clock.UtcNow; var actor = Actor(http); var token = new AgentEnrollmentToken(generated.Id, group.Id, generated.Digest, request.Label, request.ExpectedMachineName, now.AddSeconds(request.ExpiresInSeconds), actor, now); db.Add(token); db.AddRange(networks.Select(x => new AgentEnrollmentTokenAllowedNetwork(token.Id, x))); Audit(db, http, clock, "agent.enrollment-token.issued", "AgentEnrollmentToken", token.Id, new { token.Id, token.AgentGroupId, token.ExpiresAt }); await db.SaveChangesAsync(ct); return Results.Created($"/api/v1/agent-enrollment-tokens/{token.Id}", new CreateAgentEnrollmentTokenResponse(1, token.Id, generated.WireValue, group.Id, networks, token.ExpiresAt, now));
        }
        catch (DomainValidationException e) { return Problem(http, 400, AgentProblemCodes.RequestInvalid, e.Message); }
    }

    private static async Task<IResult> RevokeToken(Guid tokenId, EePulseDbContext db, IUtcClock clock, HttpContext http, CancellationToken ct)
    { var token = await db.AgentEnrollmentTokens.FindAsync([tokenId], ct); if (token is null) return Results.NoContent(); token.Revoke(clock.UtcNow); Audit(db, http, clock, "agent.enrollment-token.revoked", "AgentEnrollmentToken", tokenId, new { tokenId }); await db.SaveChangesAsync(ct); return Results.NoContent(); }

    private static async Task<IResult> Enroll(AgentEnrollmentRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, IWebHostEnvironment environment, CancellationToken ct)
    {
        if (request.SchemaVersion != 1) return Problem(http, 400, AgentProblemCodes.SchemaUnsupported, "Unsupported schema version."); if (request.ClientInstanceId == Guid.Empty || string.IsNullOrWhiteSpace(request.MachineName) || request.MachineName.Trim().Length > 255) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Enrollment identity values are outside allowed bounds."); if (!Utc(request.SentAt)) return Problem(http, 400, AgentProblemCodes.TimestampNotUtc, "sentAt must use UTC Z."); if (!SemVer().IsMatch(request.AgentVersion) || request.AgentVersion.Length > 64) return Problem(http, 426, AgentProblemCodes.AgentVersionUnsupported, "Agent version is unsupported.");
        if (!AgentSecret.TryParseAndDigest(TokenDomain, request.EnrollmentToken, out var tokenId, out var digest)) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Enrollment token format is invalid.");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct); var token = await db.AgentEnrollmentTokens.FromSqlInterpolated($"SELECT * FROM agent_enrollment_tokens WHERE id = {tokenId} FOR UPDATE").SingleOrDefaultAsync(ct); if (token is null || !AgentSecret.EqualsDigest(digest, token.Digest)) return Problem(http, 401, AgentProblemCodes.EnrollmentTokenInvalid, "Enrollment token is invalid."); if (token.IsTerminal(clock.UtcNow)) return Problem(http, 410, AgentProblemCodes.EnrollmentTokenUnavailable, "Enrollment token is unavailable."); var group = await db.AgentGroups.FindAsync([token.AgentGroupId], ct); if (group is null || !group.Enabled) return Problem(http, 409, AgentProblemCodes.AgentGroupDisabled, "Agent group is disabled.");
            var granted = await db.AgentEnrollmentTokenAllowedNetworks.Where(x => x.TokenId == token.Id).Select(x => x.Network).ToListAsync(ct); IReadOnlyList<string> local; try { local = Ipv4NetworkPolicy.Normalize(request.LocalAllowedNetworks, environment.IsDevelopment()); } catch (DomainValidationException) { return Problem(http, 403, AgentProblemCodes.NetworkPolicyMismatch, "Local network policy does not match enrollment grant."); }
            if (!Ipv4NetworkPolicy.IsNarrowerOrEqual(local, granted)) return Problem(http, 403, AgentProblemCodes.NetworkPolicyMismatch, "Local network policy does not match enrollment grant."); var groupPolicy = await db.AgentGroupAllowedNetworks.Where(x => x.AgentGroupId == group.Id).Select(x => x.Network).ToListAsync(ct); if (groupPolicy.Count == 0 || !await db.AgentConfigurationSnapshots.AnyAsync(x => x.AgentGroupId == group.Id && x.Version == group.ConfigurationVersion, ct)) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Agent group has no published network policy."); if (!Ipv4NetworkPolicy.IsNarrowerOrEqual(groupPolicy, local)) return Problem(http, 403, AgentProblemCodes.NetworkPolicyMismatch, "Local ceiling is narrower than current group policy."); if (token.ExpectedMachineName is not null && !string.Equals(token.ExpectedMachineName.Trim(), request.MachineName.Trim(), StringComparison.OrdinalIgnoreCase)) return Problem(http, 403, AgentProblemCodes.NetworkPolicyMismatch, "Machine binding does not match enrollment grant.");
            if (await db.Agents.AnyAsync(x => x.ClientInstanceId == request.ClientInstanceId && x.RevokedAt == null, ct)) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Client instance is already enrolled."); var now = clock.UtcNow; var agent = new Agent(Guid.NewGuid(), group.Id, request.ClientInstanceId, request.MachineName, request.AgentVersion, 20, now); agent.SetDesiredConfiguration(group.ConfigurationVersion); var credentialSecret = AgentSecret.Create(CredentialDomain); var expires = now.AddDays(90); var rotateAfter = now.AddDays(75); agent.SetCredentialExpiry(expires); var credential = new AgentCredential(credentialSecret.Id, agent.Id, credentialSecret.Digest, AgentCredentialState.Active, expires, rotateAfter, now); db.Add(agent); db.Add(credential); db.AddRange(local.Select(x => new AgentAllowedNetwork(agent.Id, x))); db.AddRange(groupPolicy.Select(x => new AgentPolicyAllowedNetwork(agent.Id, x))); token.Consume(agent.Id, now); AuditAgent(db, http, clock, "agent.enrollment-token.consumed", agent.Id, new { tokenId, agentId = agent.Id }); AuditAgent(db, http, clock, "agent.enrolled", agent.Id, new { agent.Id, agent.AgentGroupId, tokenId }); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Created($"/api/v1/agents/{agent.Id}", new AgentEnrollmentResponse(1, agent.Id, group.Id, credential.Id, credentialSecret.WireValue, expires, rotateAfter, now, 20, 60, agent.DesiredConfigurationVersion, $"/api/v1/agents/{agent.Id}/configuration"));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.SerializationFailure) { return Problem(http, 410, AgentProblemCodes.EnrollmentTokenUnavailable, "Enrollment token is unavailable."); }
        catch (InvalidOperationException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }) { return Problem(http, 410, AgentProblemCodes.EnrollmentTokenUnavailable, "Enrollment token is unavailable."); }
        catch (DbUpdateException) { return Problem(http, 410, AgentProblemCodes.EnrollmentTokenUnavailable, "Enrollment token is unavailable."); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private static async Task<IResult> ListAgents(EePulseDbContext db, HttpContext http, int page = 1, int pageSize = 50, Guid? agentGroupId = null, string? status = null, string? selfHealth = null, string? search = null, CancellationToken ct = default)
    { if (page < 1 || pageSize is < 1 or > 200) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Pagination values are outside allowed bounds."); var q = db.Agents.AsNoTracking(); if (agentGroupId.HasValue) q = q.Where(x => x.AgentGroupId == agentGroupId); if (Enum.TryParse<AgentStatus>(status, true, out var s)) q = q.Where(x => x.Status == s); if (Enum.TryParse<AgentSelfHealth>(selfHealth, true, out var h)) q = q.Where(x => x.SelfHealth == h); if (!string.IsNullOrWhiteSpace(search)) q = q.Where(x => EF.Functions.ILike(x.Name, $"%{search.Trim()}%") || EF.Functions.ILike(x.MachineName, $"%{search.Trim()}%")); var total = await q.LongCountAsync(ct); var agents = await q.OrderBy(x => x.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct); var ids = agents.Select(x => x.Id).ToArray(); var nets = (await db.AgentPolicyAllowedNetworks.Where(x => ids.Contains(x.AgentId)).ToListAsync(ct)).GroupBy(x => x.AgentId).ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(y => y.Network).ToArray()); return Results.Ok(new PagedAgentResponse(agents.Select(x => ToResponse(x, nets.GetValueOrDefault(x.Id, []))).ToArray(), page, pageSize, total)); }
    private static async Task<IResult> GetAgent(Guid agentId, EePulseDbContext db, HttpContext http, CancellationToken ct) { var a = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentId, ct); if (a is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent not found."); var n = await db.AgentPolicyAllowedNetworks.Where(x => x.AgentId == agentId).Select(x => x.Network).ToListAsync(ct); return Results.Ok(ToResponse(a, n)); }

    private static async Task<IResult> UpdateGroupNetworks(Guid agentGroupId, UpdateAgentGroupAllowedNetworksRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, IWebHostEnvironment env, CancellationToken ct)
    { try { ValidateSchema(request.SchemaVersion); var g = await db.AgentGroups.FindAsync([agentGroupId], ct); if (g is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent group not found."); db.Entry(g).Property(x => x.RowVersion).OriginalValue = request.RowVersion; var n = Ipv4NetworkPolicy.Normalize(request.AllowedNetworks, env.IsDevelopment()); var activeAgents = db.Agents.Where(x => x.AgentGroupId == agentGroupId && x.RevokedAt == null); var agentNets = await db.AgentAllowedNetworks.Join(activeAgents, x => x.AgentId, x => x.Id, (x, a) => x).ToListAsync(ct); if (agentNets.GroupBy(x => x.AgentId).Any(x => !Ipv4NetworkPolicy.IsNarrowerOrEqual(n, x.Select(y => y.Network)))) return Problem(http, 403, AgentProblemCodes.NetworkPolicyMismatch, "Group policy would exceed an enrolled Agent ceiling."); var policies = await db.AgentPolicyAllowedNetworks.Join(activeAgents, x => x.AgentId, x => x.Id, (x, a) => x).ToListAsync(ct); if (policies.GroupBy(x => x.AgentId).Any(x => !Ipv4NetworkPolicy.IsNarrowerOrEqual(x.Select(y => y.Network), n)) || await BuildProbes(db, g.Id, n, ct) is null) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Group policy would invalidate an affected Agent configuration."); await ReplaceGroupNetworks(db, g.Id, n, ct); var snap = await Publish(db, g, clock.UtcNow, null, ct, n, http); Audit(db, http, clock, "agent.group-networks.updated", "AgentGroup", g.Id, new { g.Id, networks = n }); await db.SaveChangesAsync(ct); return Results.Ok(new AgentNetworkPolicyResponse(1, g.Id, n, snap.Version, g.RowVersion)); } catch (DomainValidationException e) { return Problem(http, 400, AgentProblemCodes.RequestInvalid, e.Message); } catch (DbUpdateConcurrencyException) { return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Resource changed."); } }

    private static async Task<IResult> UpdateAgentNetworks(Guid agentId, UpdateAgentAllowedNetworksRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, IWebHostEnvironment env, CancellationToken ct)
    { try { ValidateSchema(request.SchemaVersion); var a = await db.Agents.FindAsync([agentId], ct); if (a is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent not found."); db.Entry(a).Property(x => x.RowVersion).OriginalValue = request.RowVersion; var n = Ipv4NetworkPolicy.Normalize(request.AllowedNetworks, env.IsDevelopment()); var ceiling = await db.AgentAllowedNetworks.Where(x => x.AgentId == agentId).Select(x => x.Network).ToListAsync(ct); var group = await db.AgentGroups.FindAsync([a.AgentGroupId], ct); var groupN = await db.AgentGroupAllowedNetworks.Where(x => x.AgentGroupId == a.AgentGroupId).Select(x => x.Network).ToListAsync(ct); if (!Ipv4NetworkPolicy.IsNarrowerOrEqual(n, ceiling) || (groupN.Count > 0 && !Ipv4NetworkPolicy.IsNarrowerOrEqual(n, groupN))) return Problem(http, 403, AgentProblemCodes.NetworkPolicyMismatch, "Remote policy cannot expand local ceiling."); if (await BuildProbes(db, a.AgentGroupId, n, ct) is null) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Agent policy would invalidate its configuration."); db.AgentPolicyAllowedNetworks.RemoveRange(await db.AgentPolicyAllowedNetworks.Where(x => x.AgentId == agentId).ToListAsync(ct)); db.AddRange(n.Select(x => new AgentPolicyAllowedNetwork(agentId, x))); var snap = await Publish(db, group!, clock.UtcNow, null, ct, null, http); a.SetDesiredConfiguration(snap.Version); Audit(db, http, clock, "agent.networks.updated", "Agent", agentId, new { agentId, networks = n }); await db.SaveChangesAsync(ct); return Results.Ok(new AgentNetworkPolicyResponse(1, agentId, n, snap.Version, a.RowVersion)); } catch (DomainValidationException e) { return Problem(http, 400, AgentProblemCodes.RequestInvalid, e.Message); } catch (DbUpdateConcurrencyException) { return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Resource changed."); } }

    private static async Task<IResult> Rollback(Guid agentGroupId, RollbackAgentConfigurationRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, CancellationToken ct)
    { if (request.SchemaVersion != 1) return Problem(http, 400, AgentProblemCodes.SchemaUnsupported, "Unsupported schema version."); if (request.SourceConfigurationVersion < 1 || request.RowVersion < 1) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Rollback values are outside allowed bounds."); var g = await db.AgentGroups.FindAsync([agentGroupId], ct); if (g is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent group not found."); db.Entry(g).Property(x => x.RowVersion).OriginalValue = request.RowVersion; var source = await db.AgentConfigurationSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.AgentGroupId == agentGroupId && x.Version == request.SourceConfigurationVersion, ct); if (source is null) return Problem(http, 410, AgentProblemCodes.ConfigurationRetired, "Configuration is not retained."); if (!ValidSnapshotDigest(source)) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Retained configuration integrity check failed."); var prior = JsonSerializer.Deserialize<SnapshotPayload>(source.Payload, CanonicalJson) ?? throw new InvalidOperationException("Retained configuration is invalid."); var version = g.PublishConfiguration(); var generated = clock.UtcNow; var content = prior with { ConfigurationVersion = version, GeneratedAt = generated, RollbackOfVersion = source.Version }; var payload = JsonSerializer.Serialize(content, CanonicalJson); var snapshot = new AgentConfigurationSnapshot(g.Id, version, payload, SnapshotDigest(payload), generated, source.Version); db.Add(snapshot); await MaterializePolicyLineage(db, g.Id, version, content.Probes, generated, ct); foreach (var a in await db.Agents.Where(x => x.AgentGroupId == g.Id && x.RevokedAt == null).ToListAsync(ct)) a.SetDesiredConfiguration(version); Audit(db, http, clock, "agent.configuration.rolled-back", "AgentGroup", g.Id, new { g.Id, version, rollbackOfVersion = source.Version }); try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Resource changed."); } return Results.Created($"/api/v1/agent-groups/{g.Id}/configuration/{version}", new AgentConfigurationPublicationResponse(1, g.Id, version, source.Version, snapshot.GeneratedAt)); }

    private static async Task<IResult> RevokeAgent(Guid agentId, RevokeAgentRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, CancellationToken ct)
    { if (request.SchemaVersion != 1) return Problem(http, 400, AgentProblemCodes.SchemaUnsupported, "Unsupported schema version."); var a = await db.Agents.FindAsync([agentId], ct); if (a is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent not found."); if (!new[] { "Compromised", "Decommissioned", "Replaced", "Administrative" }.Contains(request.ReasonCode, StringComparer.Ordinal)) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Invalid reason code."); db.Entry(a).Property(x => x.RowVersion).OriginalValue = request.RowVersion; a.Revoke(request.ReasonCode, clock.UtcNow); foreach (var c in await db.AgentCredentials.Where(x => x.AgentId == agentId && x.State != AgentCredentialState.Revoked).ToListAsync(ct)) c.Revoke(clock.UtcNow); Audit(db, http, clock, "agent.revoked", "Agent", agentId, new { agentId, reasonCode = request.ReasonCode }); try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Resource changed."); } var n = await db.AgentPolicyAllowedNetworks.Where(x => x.AgentId == agentId).Select(x => x.Network).ToListAsync(ct); return Results.Ok(ToResponse(a, n)); }

    private static async Task<IResult> Heartbeat(Guid agentId, AgentHeartbeatRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, CancellationToken ct)
    { var auth = AgentAccess(http, agentId); if (auth is not null) return auth; try { ValidateSchema(request.SchemaVersion); if (request.HeartbeatId == Guid.Empty || request.CurrentConfigurationVersion < 0 || request.QueueDepth < 0) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Heartbeat values are outside allowed bounds."); if (!Utc(request.SentAt)) return Problem(http, 400, AgentProblemCodes.TimestampNotUtc, "sentAt must use UTC Z."); if (!SemVer().IsMatch(request.AgentVersion)) return Problem(http, 426, AgentProblemCodes.AgentVersionUnsupported, "Agent version is unsupported."); db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct); var a = await db.Agents.FromSqlInterpolated($"SELECT * FROM agents WHERE id = {agentId} FOR UPDATE").SingleOrDefaultAsync(ct); if (a is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent not found."); var existing = await db.AgentHeartbeatReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agentId && x.HeartbeatId == request.HeartbeatId, ct); if (existing is not null) { await tx.CommitAsync(ct); return Results.Content(existing.ResponseJson, "application/json"); } var now = PostgresTimestamp(await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT date_trunc('microseconds', clock_timestamp()) AS \"Value\"").SingleAsync(ct)); if (!Enum.TryParse<AgentSelfHealth>(request.HealthState, true, out var health)) throw new DomainValidationException(nameof(request.HealthState), "Health state is invalid."); a.Heartbeat(request.AgentVersion, request.MachineName, request.QueueDepth, health, request.CurrentConfigurationVersion, request.SentAt, now); var credential = await db.AgentCredentials.SingleAsync(x => x.AgentId == agentId && x.State == AgentCredentialState.Active, ct); var response = new AgentHeartbeatResponse(1, request.HeartbeatId, agentId, now, now, a.HeartbeatIntervalSeconds, a.DesiredConfigurationVersion, request.CurrentConfigurationVersion != a.DesiredConfigurationVersion, now >= credential.RotateAfter, a.ClockSkewSuspected, a.ClockSkewSuspected ? "clock-skew-suspected" : null); var bytes = JsonSerializer.SerializeToUtf8Bytes(response, CanonicalJson); var json = Encoding.UTF8.GetString(bytes); db.Add(new AgentHeartbeatReceipt(agentId, request.HeartbeatId, now, json)); try { await db.SaveChangesAsync(ct); await MaterializeHeartbeatCausesAsync(db, a, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); } catch (DbUpdateConcurrencyException) { await tx.RollbackAsync(ct); return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Concurrent heartbeat changed Agent state."); } catch (DbUpdateException e) when (IsHeartbeatReceiptConflict(e)) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); var winner = await db.AgentHeartbeatReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agentId && x.HeartbeatId == request.HeartbeatId, ct); return winner is null ? Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Concurrent heartbeat receipt is not yet available.") : Results.Content(winner.ResponseJson, "application/json"); } return Results.Bytes(bytes, "application/json"); } catch (DomainValidationException e) { return Problem(http, 400, AgentProblemCodes.RequestInvalid, e.Message); } }

    private static async Task MaterializeHeartbeatCausesAsync(EePulseDbContext db, Agent agent, CancellationToken ct)
    {
        var candidates = await db.ProbeStatusProjections.AsNoTracking().Where(x => x.WatermarkAgentId == agent.Id && x.WatermarkEventAt != null && x.WatermarkResultId != null).Select(x => x.ProbeId).ToArrayAsync(ct);
        await ProbeTransactionLock.AcquireAllAsync(db, candidates, ct);
        foreach (var probeId in candidates.OrderBy(x => x.ToString("D"), StringComparer.Ordinal))
        {
            var projection = await db.ProbeStatusProjections.FromSqlInterpolated($"SELECT * FROM probe_status_projections WHERE probe_id = {probeId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (projection?.WatermarkAgentId != agent.Id || projection.WatermarkEventAt is null || projection.WatermarkResultId is null || agent.LastHeartbeatAt is null) continue;
            var ledger = await db.ProbeResultLedgerEntries.SingleAsync(x => x.AgentId == agent.Id && x.ResultId == projection.WatermarkResultId && x.ProbeId == probeId && x.EndedAt == projection.WatermarkEventAt, ct);
            var disposition = await db.ProbeResultProcessingDispositions.SingleAsync(x => x.AgentId == agent.Id && x.ResultId == ledger.ResultId && x.Disposition == ProbeResultProcessingDispositionKind.StateDriving, ct);
            if (await db.ProbeHeartbeatExpiryCauses.AnyAsync(x => x.ProbeId == probeId && x.AuthorityAgentId == agent.Id && x.SourceResultId == ledger.ResultId && x.SourceCursorEventAt == ledger.EndedAt && x.SourceLastHeartbeatReceivedAt == agent.LastHeartbeatAt && x.SourceHeartbeatIntervalSeconds == agent.HeartbeatIntervalSeconds, ct)) continue;
            db.Add(new ProbeHeartbeatExpiryCause(Guid.NewGuid(), probeId, agent.Id, ledger.ResultId, ledger.EndedAt, agent.LastHeartbeatAt.Value, agent.HeartbeatIntervalSeconds, ledger.ConfigurationVersion, agent.AgentGroupId, disposition.ResolvedPolicySnapshotId ?? throw new InvalidOperationException("State-driving result has no policy lineage."), disposition.ResolvedPolicyVersion ?? throw new InvalidOperationException("State-driving result has no policy version.")));
        }
    }

    private static async Task<IResult> GetConfiguration(Guid agentId, EePulseDbContext db, HttpContext http, CancellationToken ct)
    { var auth = AgentAccess(http, agentId); if (auth is not null) return auth; var a = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentId, ct); if (a is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent not found."); var s = await db.AgentConfigurationSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.AgentGroupId == a.AgentGroupId && x.Version == a.DesiredConfigurationVersion, ct); if (s is null) return Problem(http, 404, AgentProblemCodes.ConfigurationNotFound, "Configuration not found."); if (!ValidSnapshotDigest(s)) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Configuration integrity check failed."); var frozen = JsonSerializer.Deserialize<SnapshotPayload>(s.Payload, CanonicalJson) ?? throw new InvalidOperationException("Retained configuration is invalid."); var policy = await db.AgentPolicyAllowedNetworks.Where(x => x.AgentId == a.Id).OrderBy(x => x.Network).Select(x => x.Network).ToListAsync(ct); var ceiling = await db.AgentAllowedNetworks.Where(x => x.AgentId == a.Id).Select(x => x.Network).ToListAsync(ct); if (!Ipv4NetworkPolicy.IsNarrowerOrEqual(policy, frozen.AllowedNetworks) || !Ipv4NetworkPolicy.IsNarrowerOrEqual(policy, ceiling) || frozen.Probes.Any(p => !Ipv4NetworkPolicy.ContainsAddress(policy, p.TargetAddress))) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Retained configuration exceeds Agent network policy."); var response = new AgentConfigurationResponse(1, a.Id, a.AgentGroupId, frozen.ConfigurationVersion, frozen.GeneratedAt, frozen.RollbackOfVersion, policy, frozen.Probes); var canonical = JsonSerializer.SerializeToUtf8Bytes(response, CanonicalJson); if (canonical.Length > 2 * 1024 * 1024) return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Configuration exceeds the maximum response size."); var etag = $"\"v1-{s.Version}-{Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()}\""; http.Response.Headers.ETag = etag; if (http.Request.Headers.IfNoneMatch.Any(x => string.Equals(x, etag, StringComparison.Ordinal))) return Results.StatusCode(304); return Results.Bytes(canonical, "application/json"); }

    private static async Task<IResult> Acknowledge(Guid agentId, AgentConfigurationAcknowledgementRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, CancellationToken ct)
    {
        var auth = AgentAccess(http, agentId); if (auth is not null) return auth; if (request.SchemaVersion != 1) return Problem(http, 400, AgentProblemCodes.SchemaUnsupported, "Unsupported schema version."); if (request.AcknowledgementId == Guid.Empty || request.ConfigurationVersion < 1) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Acknowledgement values are outside allowed bounds."); if (!Utc(request.SentAt) || request.AppliedAt.HasValue && !Utc(request.AppliedAt.Value)) return Problem(http, 400, AgentProblemCodes.TimestampNotUtc, "Timestamps must use UTC Z.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var a = await db.Agents.FromSqlInterpolated($"SELECT * FROM agents WHERE id = {agentId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (a is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent not found.");
        var existing = await db.AgentConfigurationAcknowledgements.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agentId && x.Id == request.AcknowledgementId, ct);
        if (existing is not null) { await tx.CommitAsync(ct); return Results.Ok(new AgentConfigurationAcknowledgementResponse(1, existing.Id, agentId, existing.ConfigurationVersion, existing.ReceivedAt, existing.CentralEffectiveConfigurationVersion, existing.DesiredConfigurationVersion)); }
        if (request.ConfigurationVersion > a.DesiredConfigurationVersion) return Problem(http, 409, AgentProblemCodes.AcknowledgementConflict, "Acknowledgement version is newer than desired.");
        var now = PostgresTimestamp(await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct));
        if (string.Equals(request.Status, "Applied", StringComparison.Ordinal)) { if (!request.AppliedAt.HasValue || request.ErrorCode is not null) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Applied acknowledgement requires appliedAt and no errorCode."); try { a.AcknowledgeApplied(request.ConfigurationVersion, now); } catch (DomainValidationException) { return Problem(http, 409, AgentProblemCodes.AcknowledgementConflict, "Applied version cannot regress."); } }
        else if (string.Equals(request.Status, "Rejected", StringComparison.Ordinal)) { if (request.AppliedAt.HasValue || !AgentConfigurationRejectionCodes.Contains(request.ErrorCode)) return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Rejected acknowledgement requires an allowlisted errorCode."); a.RecordRejectedAcknowledgement(now); } else return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Status must be Applied or Rejected.");
        var status = Enum.Parse<AgentAcknowledgementStatus>(request.Status); var stored = new AgentConfigurationAcknowledgement(request.AcknowledgementId, agentId, request.ConfigurationVersion, status, request.AppliedAt, request.SentAt, now, request.ErrorCode, a.LastAppliedConfigurationVersion, a.DesiredConfigurationVersion); db.Add(stored);
        if (status == AgentAcknowledgementStatus.Applied && !await db.AgentConfigurationEffectiveBoundaries.AnyAsync(x => x.AgentId == agentId && x.ConfigurationVersion == request.ConfigurationVersion, ct)) db.Add(new AgentConfigurationEffectiveBoundary(agentId, request.ConfigurationVersion, stored.Id, status, now));
        AuditAgent(db, http, clock, "agent.configuration.acknowledged", agentId, new { agentId, request.AcknowledgementId, request.ConfigurationVersion, status = request.Status, errorCode = request.ErrorCode });
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); var winner = await db.AgentConfigurationAcknowledgements.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == agentId && x.Id == request.AcknowledgementId, ct); if (winner is null) return Problem(http, 409, AgentProblemCodes.AcknowledgementConflict, "Concurrent acknowledgement changed Agent state."); return Results.Ok(new AgentConfigurationAcknowledgementResponse(1, winner.Id, agentId, winner.ConfigurationVersion, winner.ReceivedAt, winner.CentralEffectiveConfigurationVersion, winner.DesiredConfigurationVersion)); }
        return Results.Ok(new AgentConfigurationAcknowledgementResponse(1, stored.Id, agentId, stored.ConfigurationVersion, stored.ReceivedAt, stored.CentralEffectiveConfigurationVersion, stored.DesiredConfigurationVersion));
    }

    private static async Task<IResult> Rotate(Guid agentId, RotateAgentCredentialRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, CancellationToken ct)
    {
        var auth = AgentAccess(http, agentId); if (auth is not null) return auth; if (request.SchemaVersion != 1) return Problem(http, 400, AgentProblemCodes.SchemaUnsupported, "Unsupported schema version.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        _ = await db.Agents.FromSqlInterpolated($"SELECT * FROM agents WHERE id = {agentId} FOR UPDATE").SingleOrDefaultAsync(ct)
          ?? throw new DomainValidationException(nameof(agentId), "Agent not found.");
        var now = clock.UtcNow; var pending = await db.AgentCredentials.SingleOrDefaultAsync(x => x.AgentId == agentId && x.State == AgentCredentialState.Pending, ct);
        pending?.Revoke(now);
        var secret = AgentSecret.Create(CredentialDomain); var expires = now.AddDays(90); var rotateAfter = now.AddDays(75); db.Add(new AgentCredential(secret.Id, agentId, secret.Digest, AgentCredentialState.Pending, expires, rotateAfter, now)); AuditAgent(db, http, clock, "agent.credential.rotation-requested", agentId, new { agentId, credentialId = secret.Id });
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Created($"/api/v1/agents/{agentId}/credentials/{secret.Id}", new RotateAgentCredentialResponse(1, secret.Id, secret.WireValue, expires, rotateAfter)); }
        catch (DbUpdateException) { await tx.RollbackAsync(ct); return Problem(http, 409, AgentProblemCodes.ConfigurationConflict, "Credential rotation conflicted with another request."); }
    }

    private static async Task<IResult> IngestResults(Guid agentId, ProbeResultIngestionBatchRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http, CancellationToken ct)
    {
        var auth = AgentAccess(http, agentId); if (auth is not null) return auth;
        if (request.BatchId == Guid.Empty || request.Results is null || request.Results.Count > 1000)
            return Problem(http, 400, AgentProblemCodes.RequestInvalid, "Result batch values are outside allowed bounds.");
        if (request.Results.Any(x => x is null))
            return Results.Ok(new ProbeResultIngestionBatchResponse(request.BatchId, [], request.Results.Where(x => x is null).Select(_ => new RejectedProbeResultIngestion(Guid.Empty, "result-invalid")).ToArray()));
        if (request.Results.Any(x => x.AgentId != agentId))
            return Problem(http, 403, AgentProblemCodes.AgentIdentityMismatch, "Agent identity does not match authenticated credential.");

        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentId, ct);
        if (agent is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent not found.");
        var rejected = new List<RejectedProbeResultIngestion>();
        var candidates = new Dictionary<Guid, IngestionCandidate>();
        var conflictingResultIds = new HashSet<Guid>();
        var snapshots = new Dictionary<long, SnapshotPayload?>();
        foreach (var result in request.Results)
        {
            var rejection = await ValidateIngestionResult(result, agent, db, snapshots, ct);
            if (rejection is not null) { rejected.Add(new RejectedProbeResultIngestion(result.ResultId, rejection)); continue; }
            var immutable = ToImmutablePayload(result);
            var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(immutable, CanonicalJson));
            if (conflictingResultIds.Contains(result.ResultId)) { CryptographicOperations.ZeroMemory(digest); continue; }
            if (candidates.TryGetValue(result.ResultId, out var prior))
            {
                if (CryptographicOperations.FixedTimeEquals(prior.Digest, digest)) { CryptographicOperations.ZeroMemory(digest); continue; }
                CryptographicOperations.ZeroMemory(prior.Digest); CryptographicOperations.ZeroMemory(digest); candidates.Remove(result.ResultId); conflictingResultIds.Add(result.ResultId);
                rejected.Add(new RejectedProbeResultIngestion(result.ResultId, "result-identity-conflict"));
                continue;
            }
            candidates.Add(result.ResultId, new IngestionCandidate(result, digest));
        }

        var accepted = new List<Guid>();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var lockedReceivingAgent = await db.Agents.FromSqlInterpolated($"SELECT * FROM agents WHERE id = {agentId} FOR SHARE").SingleOrDefaultAsync(ct);
        if (lockedReceivingAgent is null) return Problem(http, 404, AgentProblemCodes.AgentNotFound, "Agent not found.");
        if (lockedReceivingAgent.Id != agentId) return Problem(http, 403, AgentProblemCodes.AgentIdentityMismatch, "Agent identity does not match authenticated credential.");
        foreach (var resultId in conflictingResultIds.OrderBy(x => x))
            AuditResultIdentityConflict(db, http, clock, agentId, resultId);
        await ProbeTransactionLock.AcquireAllAsync(db, candidates.Values.Select(x => x.Result.ProbeId), ct);
        var receivedAt = PostgresTimestamp(await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct));
        foreach (var candidate in candidates.Values.OrderBy(x => x.Result.ResultId))
        {
            var result = candidate.Result; var key = $"{agentId:D}/{result.ResultId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            var existing = await db.ProbeResultLedgerEntries.SingleOrDefaultAsync(x => x.AgentId == agentId && x.ResultId == result.ResultId, ct);
            if (existing is not null)
            {
                if (CryptographicOperations.FixedTimeEquals(existing.ImmutablePayloadDigest, candidate.Digest)) accepted.Add(result.ResultId);
                else
                {
                    rejected.Add(new RejectedProbeResultIngestion(result.ResultId, "result-identity-conflict"));
                    AuditResultIdentityConflict(db, http, clock, agentId, result.ResultId);
                }
                CryptographicOperations.ZeroMemory(candidate.Digest);
                continue;
            }
            db.Add(new ProbeResultLedgerEntry(agentId, result.ResultId, result.ProbeId, result.ConfigurationVersion,
                PostgresTimestamp(result.StartedAt), PostgresTimestamp(result.EndedAt), result.AttemptCount, result.SuccessfulAttemptCount, result.PacketLossRatio,
                result.MinRttMilliseconds, result.AverageRttMilliseconds, result.MaxRttMilliseconds, result.ErrorCategory, candidate.Digest, receivedAt));
            accepted.Add(result.ResultId);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new ProbeResultIngestionBatchResponse(request.BatchId, accepted.OrderBy(x => x).ToArray(), rejected.OrderBy(x => x.ResultId).ToArray()));
    }

    private static async Task<string?> ValidateIngestionResult(ProbeResultIngestionEnvelope result, Agent agent, EePulseDbContext db, Dictionary<long, SnapshotPayload?> snapshots, CancellationToken ct)
    {
        if (result.ResultSchemaVersion != 1) return AgentProblemCodes.SchemaUnsupported;
        if (result.ResultId == Guid.Empty || result.AgentId == Guid.Empty || result.ProbeId == Guid.Empty || result.ConfigurationVersion < 1 || result.AttemptCount < 1 || result.SuccessfulAttemptCount < 0 || result.SuccessfulAttemptCount > result.AttemptCount) return "result-invalid";
        if (!Utc(result.StartedAt) || !Utc(result.EndedAt)) return AgentProblemCodes.TimestampNotUtc;
        if (result.EndedAt < result.StartedAt || result.PacketLossRatio is < 0 or > 1 || !ValidRtts(result)) return "result-invalid";
        if (result.ErrorCategory is not null && !ResultErrorCategories.Contains(result.ErrorCategory)) return "error-category-invalid";
        if (agent.LastAppliedConfigurationVersion < result.ConfigurationVersion) return "configuration-lineage-invalid";
        if (!snapshots.TryGetValue(result.ConfigurationVersion, out var snapshot))
        {
            var stored = await db.AgentConfigurationSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.AgentGroupId == agent.AgentGroupId && x.Version == result.ConfigurationVersion, ct);
            snapshot = stored is not null && ValidSnapshotDigest(stored) ? JsonSerializer.Deserialize<SnapshotPayload>(stored.Payload, CanonicalJson) : null;
            snapshots[result.ConfigurationVersion] = snapshot;
        }
        return snapshot is null || !snapshot.Probes.Any(x => x.ProbeId == result.ProbeId) ? "target-identity-invalid" : null;
    }

    private static bool ValidRtts(ProbeResultIngestionEnvelope result) =>
        ValidRtt(result.MinRttMilliseconds) && ValidRtt(result.AverageRttMilliseconds) && ValidRtt(result.MaxRttMilliseconds) &&
        (!result.MinRttMilliseconds.HasValue || !result.AverageRttMilliseconds.HasValue || result.MinRttMilliseconds <= result.AverageRttMilliseconds) &&
        (!result.AverageRttMilliseconds.HasValue || !result.MaxRttMilliseconds.HasValue || result.AverageRttMilliseconds <= result.MaxRttMilliseconds);
    private static bool ValidRtt(decimal? value) => !value.HasValue || value.Value >= 0 && value.Value <= 999999999999.999999m && decimal.Round(value.Value, 6, MidpointRounding.ToZero) == value.Value;
    private static ImmutableResultPayload ToImmutablePayload(ProbeResultIngestionEnvelope result) => new(result.ResultSchemaVersion, result.ResultId, result.AgentId, result.ProbeId, result.ConfigurationVersion, result.StartedAt, result.EndedAt, result.AttemptCount, result.SuccessfulAttemptCount, result.PacketLossRatio, result.MinRttMilliseconds, result.AverageRttMilliseconds, result.MaxRttMilliseconds, result.ErrorCategory);
    private static readonly HashSet<string> ResultErrorCategories = new(StringComparer.Ordinal) { "Timeout", "Unreachable", "PermissionDenied", "InvalidTarget", "NetworkUnavailable", "Cancelled", "TransportError" };

    private static async Task ReplaceGroupNetworks(EePulseDbContext db, Guid id, IReadOnlyList<string> n, CancellationToken ct) { db.RemoveRange(await db.AgentGroupAllowedNetworks.Where(x => x.AgentGroupId == id).ToListAsync(ct)); db.AddRange(n.Select(x => new AgentGroupAllowedNetwork(id, x))); }
    internal static async Task<AgentConfigurationSnapshot> Publish(EePulseDbContext db, EePulse.Domain.Inventory.AgentGroup g, DateTimeOffset now, long? rollback, CancellationToken ct, IReadOnlyList<string>? candidateNetworks = null, HttpContext? http = null) { var version = g.PublishConfiguration(); var networks = (candidateNetworks ?? await db.AgentGroupAllowedNetworks.Where(x => x.AgentGroupId == g.Id).Select(x => x.Network).ToListAsync(ct)).OrderBy(x => x, StringComparer.Ordinal).ToArray(); var probes = await BuildProbes(db, g.Id, networks, ct) ?? throw new AgentConfigurationPublicationException(http?.TraceIdentifier, http?.Request.Path.Value); var content = new SnapshotPayload(1, g.Id, version, now, rollback, networks, probes); var payload = JsonSerializer.Serialize(content, CanonicalJson); var s = new AgentConfigurationSnapshot(g.Id, version, payload, SnapshotDigest(payload), now, rollback); db.Add(s); await MaterializePolicyLineage(db, g.Id, version, content.Probes, now, ct); Guid? actor = http is not null && Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null; db.Add(new AuditEvent(Guid.NewGuid(), actor, "agent.configuration.published", "AgentGroup", g.Id, null, JsonSerializer.Serialize(new { agentGroupId = g.Id, version, rollbackOfVersion = rollback }), http?.TraceIdentifier ?? "system-configuration-publication", now, http?.Connection.RemoteIpAddress?.ToString())); foreach (var a in await db.Agents.Where(x => x.AgentGroupId == g.Id && x.RevokedAt == null).ToListAsync(ct)) a.SetDesiredConfiguration(version); return s; }
    private static async Task MaterializePolicyLineage(EePulseDbContext db, Guid agentGroupId, long configurationVersion, IReadOnlyList<AgentProbeConfiguration> probes, DateTimeOffset createdAt, CancellationToken ct)
    {
        foreach (var policy in probes.GroupBy(probe => new
        {
            probe.FailureThreshold,
            probe.RecoveryThreshold,
            probe.WarningRttMilliseconds,
            WarningPacketLossRatio = 0.05m,
            PolicyVersion = 1,
            ApprovedLatenessSeconds = 300,
            ApprovedFutureSkewSeconds = 60,
        }))
        {
            var content = policy.Key;
            var snapshot = await db.ProbeStatusPolicySnapshots.Where(row =>
                row.PolicyVersion == content.PolicyVersion &&
                row.FailureThreshold == content.FailureThreshold &&
                row.RecoveryThreshold == content.RecoveryThreshold &&
                row.WarningRttMilliseconds == content.WarningRttMilliseconds &&
                row.WarningPacketLossRatio == content.WarningPacketLossRatio &&
                row.ApprovedLatenessSeconds == content.ApprovedLatenessSeconds &&
                row.ApprovedFutureSkewSeconds == content.ApprovedFutureSkewSeconds)
                .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).FirstOrDefaultAsync(ct);
            snapshot ??= new ProbeStatusPolicySnapshot(Guid.NewGuid(), content.PolicyVersion, content.FailureThreshold,
                content.RecoveryThreshold, content.WarningRttMilliseconds, content.WarningPacketLossRatio, createdAt);
            if (db.Entry(snapshot).State == EntityState.Detached) db.Add(snapshot);
            foreach (var probe in policy)
                db.Add(new ProbeStatusPolicyBinding(probe.ProbeId, configurationVersion, agentGroupId, snapshot.Id));
        }
    }
    internal static async Task<IReadOnlyList<AgentProbeConfiguration>?> BuildProbes(EePulseDbContext db, Guid groupId, IReadOnlyList<string> networks, CancellationToken ct) { if (!await db.AgentGroups.Where(g => g.Id == groupId).Select(g => g.Enabled).SingleAsync(ct)) return []; var rows = await db.Probes.Where(p => p.AgentGroupId == groupId && p.Enabled).Join(db.Devices.Where(d => d.Enabled), p => p.DeviceId, d => d.Id, (p, d) => new { p, d }).OrderBy(x => x.p.Id).Take(2001).ToListAsync(ct); if (rows.Count > 2000) return null; if (rows.Any(x => !Ipv4NetworkPolicy.ContainsAddress(networks, x.d.Address))) return null; return rows.Select(x => new AgentProbeConfiguration(x.p.Id, x.d.Id, x.p.ConfigVersion, "icmp", x.d.Address, x.p.IntervalSeconds, x.p.TimeoutMilliseconds, x.p.AttemptCount, x.p.WarningRttMilliseconds, x.p.CriticalRttMilliseconds, x.p.FailureThreshold, x.p.RecoveryThreshold)).ToArray(); }
    private static AgentResponse ToResponse(Agent a, IReadOnlyList<string> n) => new(1, a.Id, a.AgentGroupId, a.Name, a.MachineName, a.AgentVersion, a.Status.ToString(), a.SelfHealth.ToString(), a.QueueDepth, n, a.LastHeartbeatAt, a.LastReportedAt, a.DesiredConfigurationVersion, a.LastAppliedConfigurationVersion, a.LastConfigurationAcknowledgedAt, a.ClockSkewSuspected, a.CredentialExpiresAt, a.CreatedAt, a.RevokedAt, a.RowVersion);
    private static IResult? AgentAccess(HttpContext http, Guid route) { var claim = http.User.FindFirstValue("agent_id"); if (!Guid.TryParse(claim, out var id)) return Problem(http, 401, AgentProblemCodes.AgentAuthenticationRequired, "Agent authentication is required."); if (bool.TryParse(http.User.FindFirstValue("agent_revoked"), out var revoked) && revoked) return Problem(http, 410, AgentProblemCodes.AgentRevoked, "Agent is revoked."); return id == route ? null : Problem(http, 403, AgentProblemCodes.AgentIdentityMismatch, "Agent identity does not match route."); }
    private static IResult Problem(HttpContext http, int status, string code, string detail) => Results.Problem(detail, type: $"https://ee-pulse.invalid/problems/{code}", statusCode: status, title: code, instance: http.Request.Path, extensions: new Dictionary<string, object?> { { "code", code }, { "retryable", false }, { "correlationId", http.TraceIdentifier } });
    private static bool IsHeartbeatReceiptConflict(DbUpdateException e) => e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && string.Equals(pg.ConstraintName, "PK_agent_heartbeat_receipts", StringComparison.Ordinal);
    private static void ValidateSchema(int value) { if (value != 1) throw new DomainValidationException(nameof(value), "Unsupported schema version."); }
    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;
    private static DateTimeOffset PostgresTimestamp(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    private static Guid Actor(HttpContext h) => Guid.TryParse(h.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty ? id : throw new UnauthorizedAccessException();
    private static void Audit(EePulseDbContext db, HttpContext h, IUtcClock clock, string action, string type, Guid id, object metadata) => db.Add(new AuditEvent(Guid.NewGuid(), h.User.Identity?.AuthenticationType == AgentContract.CredentialAuthenticationScheme ? null : Actor(h), action, type, id, null, JsonSerializer.Serialize(metadata), h.TraceIdentifier, clock.UtcNow, h.Connection.RemoteIpAddress?.ToString()));
    private static void AuditAgent(EePulseDbContext db, HttpContext h, IUtcClock clock, string action, Guid id, object metadata) => db.Add(new AuditEvent(Guid.NewGuid(), null, action, "Agent", id, null, JsonSerializer.Serialize(metadata), h.TraceIdentifier, clock.UtcNow, h.Connection.RemoteIpAddress?.ToString()));
    private static void AuditResultIdentityConflict(EePulseDbContext db, HttpContext h, IUtcClock clock, Guid agentId, Guid resultId) => db.Add(new AuditEvent(Guid.NewGuid(), null, "agent.result.identity-conflict", "Agent", agentId, null, JsonSerializer.Serialize(new { agentId, resultId, reasonCode = "immutable-payload-digest-mismatch" }), h.TraceIdentifier, clock.UtcNow, null));
    private static bool ValidSnapshotDigest(AgentConfigurationSnapshot snapshot) => CryptographicOperations.FixedTimeEquals(SnapshotDigest(snapshot.Payload), snapshot.PayloadDigest);
    private static byte[] SnapshotDigest(string payload) { using var document = JsonDocument.Parse(payload); var buffer = new System.Buffers.ArrayBufferWriter<byte>(); using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(document.RootElement, writer); return SHA256.HashData(buffer.WrittenSpan); }
    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    { switch (value.ValueKind) { case JsonValueKind.Object: writer.WriteStartObject(); foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteCanonical(property.Value, writer); } writer.WriteEndObject(); break; case JsonValueKind.Array: writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer); writer.WriteEndArray(); break; case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break; case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText()); break; case JsonValueKind.True: writer.WriteBooleanValue(true); break; case JsonValueKind.False: writer.WriteBooleanValue(false); break; default: writer.WriteNullValue(); break; } }
    private static JsonSerializerOptions CreateCanonicalJson() { var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); AgentJsonContract.AddConverters(options); return options; }
    private sealed record SnapshotPayload(int SchemaVersion, Guid AgentGroupId, long ConfigurationVersion, DateTimeOffset GeneratedAt, long? RollbackOfVersion, IReadOnlyList<string> AllowedNetworks, IReadOnlyList<AgentProbeConfiguration> Probes);
    private sealed record ImmutableResultPayload(int ResultSchemaVersion, Guid ResultId, Guid AgentId, Guid ProbeId, long ConfigurationVersion, DateTimeOffset StartedAt, DateTimeOffset EndedAt, int AttemptCount, int SuccessfulAttemptCount, decimal PacketLossRatio, decimal? MinRttMilliseconds, decimal? AverageRttMilliseconds, decimal? MaxRttMilliseconds, string? ErrorCategory);
    private sealed record IngestionCandidate(ProbeResultIngestionEnvelope Result, byte[] Digest);
    internal sealed class AgentConfigurationPublicationException(string? correlationId, string? instance) : Exception
    { public string CorrelationId { get; } = correlationId ?? Guid.NewGuid().ToString("N"); public string Instance { get; } = instance ?? "/api/v1"; }
    [GeneratedRegex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex SemVer();
}
