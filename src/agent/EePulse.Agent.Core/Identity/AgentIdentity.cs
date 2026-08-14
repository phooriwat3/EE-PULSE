namespace EePulse.Agent.Core.Identity;

public sealed record AgentCredential(Guid CredentialId, string Secret, DateTimeOffset ExpiresAt, DateTimeOffset RotateAfter);

public sealed record AgentIdentity(
    Guid AgentId,
    Guid AgentGroupId,
    Guid ClientInstanceId,
    string MachineName,
    string AgentVersion,
    IReadOnlyList<string> LocalAllowedNetworks,
    AgentCredential ActiveCredential,
    AgentCredential? PendingCredential,
    int HeartbeatIntervalSeconds,
    int HeartbeatExpiresAfterSeconds,
    long DesiredConfigurationVersion,
    bool IsRevoked = false)
{
    public AgentCredential AuthenticationCredential => PendingCredential ?? ActiveCredential;
}

public interface IAgentIdentityStore
{
    ValueTask<AgentIdentity?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(AgentIdentity identity, CancellationToken cancellationToken);

    ValueTask DeleteAsync(CancellationToken cancellationToken);
}
