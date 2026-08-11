using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using EePulse.Api.Agents;
using EePulse.Contracts.Agents;
using EePulse.Domain.Agents;
using EePulse.Infrastructure.Persistence;
using EePulse.Application.Time;
using EePulse.Domain.Auditing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EePulse.Api.Authorization;

public sealed class AgentCredentialAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    EePulseDbContext db, IUtcClock clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!AgentSecret.TryParseAndDigest("EE-Pulse-Agent-Credential-v1", header[7..].Trim(), out var id, out var digest)) return AuthenticateResult.Fail("Invalid Agent credential.");
        try
        {
            var credential = await db.AgentCredentials.SingleOrDefaultAsync(x => x.Id == id, Context.RequestAborted);
            if (credential is null || !AgentSecret.EqualsDigest(digest, credential.Digest)) return AuthenticateResult.Fail("Invalid Agent credential.");
            var agent = await db.Agents.SingleAsync(x => x.Id == credential.AgentId, Context.RequestAborted);
            if (agent.RevokedAt.HasValue) return Success(agent.Id, true);
            if (credential.State == AgentCredentialState.Revoked) return AuthenticateResult.Fail("Revoked Agent credential.");
            if (credential.ExpiresAt <= clock.UtcNow) return AuthenticateResult.Fail("Expired Agent credential.");
            if (credential.State == AgentCredentialState.Pending && credential.PendingExpiresAt <= clock.UtcNow)
                return AuthenticateResult.Fail("Expired pending Agent credential.");
            if (credential.State == AgentCredentialState.Pending)
            {
                var agentId=agent.Id;
                await using var transaction = await db.Database.BeginTransactionAsync(Context.RequestAborted);
                db.ChangeTracker.Clear();
                var lockedAgent=await db.Agents.FromSqlInterpolated($"SELECT * FROM agents WHERE id = {agentId} FOR UPDATE").SingleAsync(Context.RequestAborted);
                var locked = await db.AgentCredentials.FromSqlInterpolated($"SELECT * FROM agent_credentials WHERE agent_id = {agentId} FOR UPDATE").ToListAsync(Context.RequestAborted);
                var replacement = locked.Single(x => x.Id == credential.Id);
                if(replacement.State==AgentCredentialState.Pending&&(replacement.PendingExpiresAt<=clock.UtcNow||replacement.ExpiresAt<=clock.UtcNow))
                {await transaction.RollbackAsync(Context.RequestAborted);return AuthenticateResult.Fail("Expired pending Agent credential.");}
                if (replacement.State == AgentCredentialState.Pending)
                {
                    var active = locked.SingleOrDefault(x => x.State == AgentCredentialState.Active);
                    active?.Revoke(clock.UtcNow); replacement.Promote(clock.UtcNow); lockedAgent.SetCredentialExpiry(replacement.ExpiresAt);
                    db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), null, "agent.credential.promoted", "Agent", lockedAgent.Id, null,
                        System.Text.Json.JsonSerializer.Serialize(new { agentId = lockedAgent.Id, credentialId = replacement.Id, replacedCredentialId = active?.Id }),
                        Context.TraceIdentifier, clock.UtcNow, Context.Connection.RemoteIpAddress?.ToString()));
                    await db.SaveChangesAsync(Context.RequestAborted);
                }
                await transaction.CommitAsync(Context.RequestAborted);
            }
            return Success(agent.Id, false);
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }
    private static AuthenticateResult Success(Guid agentId, bool revoked)
    { var claims = new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()), new Claim("agent_id", agentId.ToString()), new Claim("agent_revoked", revoked.ToString()) }; var identity = new ClaimsIdentity(claims, AgentContract.CredentialAuthenticationScheme); return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AgentContract.CredentialAuthenticationScheme)); }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/problem+json";
        return Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { type = $"https://ee-pulse.invalid/problems/{AgentProblemCodes.AgentAuthenticationRequired}", title = AgentProblemCodes.AgentAuthenticationRequired, status = 401, detail = "Agent authentication is required or invalid.", instance = Request.Path.Value, code = AgentProblemCodes.AgentAuthenticationRequired, retryable = false, correlationId = Context.TraceIdentifier }));
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/problem+json";
        return Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { type = $"https://ee-pulse.invalid/problems/{AgentProblemCodes.AgentIdentityMismatch}", title = AgentProblemCodes.AgentIdentityMismatch, status = 403, detail = "Agent identity is forbidden for this operation.", instance = Request.Path.Value, code = AgentProblemCodes.AgentIdentityMismatch, retryable = false, correlationId = Context.TraceIdentifier }));
    }
}
