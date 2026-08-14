using EePulse.Domain.Common;

namespace EePulse.Domain.Agents;

public enum AgentSelfHealth { Unknown, Healthy, Degraded, Unhealthy }
public enum AgentStatus { Pending, Online, Offline, Revoked }
public enum AgentCredentialState { Active, Pending, Revoked }
public enum AgentAcknowledgementStatus { Applied, Rejected }

public sealed class Agent
{
    private Agent() { }

    public Agent(Guid id, Guid groupId, Guid clientInstanceId, string machineName, string version,
        int heartbeatIntervalSeconds, DateTimeOffset now)
    {
        Id = Required(id, nameof(id));
        AgentGroupId = Required(groupId, nameof(groupId));
        ClientInstanceId = Required(clientInstanceId, nameof(clientInstanceId));
        MachineName = Guard.Required(machineName, nameof(machineName), 255);
        Name = MachineName;
        AgentVersion = Guard.Required(version, nameof(version), 64);
        HeartbeatIntervalSeconds = Guard.Range(heartbeatIntervalSeconds, nameof(heartbeatIntervalSeconds), 15, 30);
        SelfHealth = AgentSelfHealth.Unknown;
        Status = AgentStatus.Pending;
        CreatedAt = Guard.Utc(now, nameof(now));
    }

    public Guid Id { get; private set; }
    public Guid AgentGroupId { get; private set; }
    public Guid ClientInstanceId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string MachineName { get; private set; } = string.Empty;
    public string AgentVersion { get; private set; } = string.Empty;
    public AgentSelfHealth SelfHealth { get; private set; }
    public AgentStatus Status { get; private set; }
    public long QueueDepth { get; private set; }
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
    public DateTimeOffset? LastReportedAt { get; private set; }
    public int HeartbeatIntervalSeconds { get; private set; }
    public long DesiredConfigurationVersion { get; private set; }
    public long LastAppliedConfigurationVersion { get; private set; }
    public DateTimeOffset? LastConfigurationAcknowledgedAt { get; private set; }
    public bool ClockSkewSuspected { get; private set; }
    public DateTimeOffset? CredentialExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }
    public long RowVersion { get; private set; }

    public void SetCredentialExpiry(DateTimeOffset expiresAt) => CredentialExpiresAt = Guard.Utc(expiresAt, nameof(expiresAt));
    public void SetDesiredConfiguration(long version)
    {
        if (version < DesiredConfigurationVersion) throw new DomainValidationException(nameof(version), "Desired configuration cannot regress.");
        DesiredConfigurationVersion = version;
    }
    public void Heartbeat(string version, string machineName, long queueDepth, AgentSelfHealth health,
        long currentVersion, DateTimeOffset sentAt, DateTimeOffset receivedAt)
    {
        if (RevokedAt.HasValue) throw new InvalidOperationException("Agent is revoked.");
        AgentVersion = Guard.Required(version, nameof(version), 64);
        MachineName = Guard.Required(machineName, nameof(machineName), 255);
        QueueDepth = queueDepth < 0 ? throw new DomainValidationException(nameof(queueDepth), "Queue depth cannot be negative.") : queueDepth;
        SelfHealth = health is AgentSelfHealth.Healthy or AgentSelfHealth.Degraded or AgentSelfHealth.Unhealthy
            ? health : throw new DomainValidationException(nameof(health), "Health state is invalid.");
        sentAt = Guard.Utc(sentAt, nameof(sentAt));
        receivedAt = Guard.Utc(receivedAt, nameof(receivedAt));
        LastReportedAt = sentAt;
        if (!LastHeartbeatAt.HasValue || receivedAt > LastHeartbeatAt) LastHeartbeatAt = receivedAt;
        Status = AgentStatus.Online;
        ClockSkewSuspected = (receivedAt - sentAt).Duration() > TimeSpan.FromMinutes(5);
        _ = currentVersion; // self-reported state never advances central effective configuration.
    }
    public void AcknowledgeApplied(long version, DateTimeOffset receivedAt)
    {
        if (version < LastAppliedConfigurationVersion || version > DesiredConfigurationVersion)
            throw new DomainValidationException(nameof(version), "Applied configuration version conflicts with central state.");
        LastAppliedConfigurationVersion = version;
        LastConfigurationAcknowledgedAt = Guard.Utc(receivedAt, nameof(receivedAt));
    }
    public void RecordRejectedAcknowledgement(DateTimeOffset receivedAt) =>
        LastConfigurationAcknowledgedAt = Guard.Utc(receivedAt, nameof(receivedAt));
    public void Revoke(string reason, DateTimeOffset now)
    {
        if (!RevokedAt.HasValue) { RevokedAt = Guard.Utc(now, nameof(now)); RevocationReason = Guard.Required(reason, nameof(reason), 50); Status = AgentStatus.Revoked; }
    }
    public bool MarkOffline(DateTimeOffset now)
    { if (Status != AgentStatus.Online || !LastHeartbeatAt.HasValue) return false; var expiry = TimeSpan.FromSeconds(Math.Max(60, 3 * HeartbeatIntervalSeconds)); if (now - LastHeartbeatAt.Value < expiry) return false; Status = AgentStatus.Offline; return true; }
    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}

public sealed class AgentAllowedNetwork
{
    private AgentAllowedNetwork() { }
    public AgentAllowedNetwork(Guid agentId, string network) { AgentId = agentId; Network = Guard.Required(network, nameof(network), 18); }
    public Guid AgentId { get; private set; }
    public string Network { get; private set; } = string.Empty;
}

public sealed class AgentPolicyAllowedNetwork
{
    private AgentPolicyAllowedNetwork() { }
    public AgentPolicyAllowedNetwork(Guid agentId, string network) { AgentId = agentId; Network = Guard.Required(network, nameof(network), 18); }
    public Guid AgentId { get; private set; }
    public string Network { get; private set; } = string.Empty;
}

public sealed class AgentGroupAllowedNetwork
{
    private AgentGroupAllowedNetwork() { }
    public AgentGroupAllowedNetwork(Guid groupId, string network) { AgentGroupId = groupId; Network = Guard.Required(network, nameof(network), 18); }
    public Guid AgentGroupId { get; private set; }
    public string Network { get; private set; } = string.Empty;
}

public sealed class AgentEnrollmentTokenAllowedNetwork
{
    private AgentEnrollmentTokenAllowedNetwork() { }
    public AgentEnrollmentTokenAllowedNetwork(Guid tokenId, string network) { TokenId = tokenId; Network = Guard.Required(network, nameof(network), 18); }
    public Guid TokenId { get; private set; }
    public string Network { get; private set; } = string.Empty;
}

public sealed class AgentEnrollmentToken
{
    private AgentEnrollmentToken() { }
    public AgentEnrollmentToken(Guid id, Guid groupId, byte[] digest, string label, string? machineName,
        DateTimeOffset expiresAt, Guid creatorId, DateTimeOffset now)
    {
        Id = id; AgentGroupId = groupId; Digest = RequireDigest(digest); Label = Guard.Required(label, nameof(label), 200);
        ExpectedMachineName = Guard.Optional(machineName, nameof(machineName), 255); ExpiresAt = Guard.Utc(expiresAt, nameof(expiresAt));
        CreatedBy = creatorId; CreatedAt = Guard.Utc(now, nameof(now));
    }
    public Guid Id { get; private set; }
    public Guid AgentGroupId { get; private set; }
    public byte[] Digest { get; private set; } = [];
    public string Label { get; private set; } = string.Empty;
    public string? ExpectedMachineName { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public Guid? UsedByAgentId { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public long RowVersion { get; private set; }
    public bool IsTerminal(DateTimeOffset now) => UsedAt.HasValue || RevokedAt.HasValue || now >= ExpiresAt;
    public void Consume(Guid agentId, DateTimeOffset now) { if (IsTerminal(now)) throw new InvalidOperationException("Token unavailable."); UsedAt = now; UsedByAgentId = agentId; }
    public void Revoke(DateTimeOffset now) { if (!UsedAt.HasValue && !RevokedAt.HasValue) RevokedAt = now; }
    private static byte[] RequireDigest(byte[] value) => value.Length == 32 ? value.ToArray() : throw new DomainValidationException(nameof(value), "Digest must contain 32 bytes.");
}

public sealed class AgentCredential
{
    private AgentCredential() { }
    public AgentCredential(Guid id, Guid agentId, byte[] digest, AgentCredentialState state, DateTimeOffset expiresAt,
        DateTimeOffset rotateAfter, DateTimeOffset now)
    { Id = id; AgentId = agentId; Digest = digest.Length == 32 ? digest.ToArray() : throw new DomainValidationException(nameof(digest), "Digest must contain 32 bytes."); State = state; ExpiresAt = expiresAt; RotateAfter = rotateAfter; CreatedAt = now; PendingExpiresAt = state == AgentCredentialState.Pending ? now.AddHours(24) : null; }
    public Guid Id { get; private set; }
    public Guid AgentId { get; private set; }
    public byte[] Digest { get; private set; } = [];
    public AgentCredentialState State { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset RotateAfter { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PendingExpiresAt { get; private set; }
    public DateTimeOffset? FirstUsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public void Promote(DateTimeOffset now) { State = AgentCredentialState.Active; FirstUsedAt ??= now; PendingExpiresAt = null; }
    public void Revoke(DateTimeOffset now) { State = AgentCredentialState.Revoked; RevokedAt ??= now; }
}

public sealed class AgentConfigurationSnapshot
{
    private AgentConfigurationSnapshot() { }
    public AgentConfigurationSnapshot(Guid groupId, long version, string payload, byte[] digest, DateTimeOffset generatedAt, long? rollbackOfVersion)
    { AgentGroupId = groupId; Version = version; Payload = Guard.Required(payload, nameof(payload), 2_097_152); PayloadDigest = digest.Length == 32 ? digest.ToArray() : throw new DomainValidationException(nameof(digest), "Digest must contain 32 bytes."); GeneratedAt = generatedAt; RollbackOfVersion = rollbackOfVersion; }
    public Guid AgentGroupId { get; private set; }
    public long Version { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public byte[] PayloadDigest { get; private set; } = [];
    public DateTimeOffset GeneratedAt { get; private set; }
    public long? RollbackOfVersion { get; private set; }
}

public sealed class AgentConfigurationAcknowledgement
{
    private AgentConfigurationAcknowledgement() { }
    public AgentConfigurationAcknowledgement(Guid id, Guid agentId, long version, AgentAcknowledgementStatus status,
        DateTimeOffset? appliedAt, DateTimeOffset sentAt, DateTimeOffset receivedAt, string? errorCode,
        long centralEffectiveConfigurationVersion, long desiredConfigurationVersion)
    { Id = id; AgentId = agentId; ConfigurationVersion = version; Status = status; AppliedAt = appliedAt; SentAt = sentAt; ReceivedAt = receivedAt; ErrorCode = errorCode; CentralEffectiveConfigurationVersion = centralEffectiveConfigurationVersion; DesiredConfigurationVersion = desiredConfigurationVersion; }
    public Guid Id { get; private set; }
    public Guid AgentId { get; private set; }
    public long ConfigurationVersion { get; private set; }
    public AgentAcknowledgementStatus Status { get; private set; }
    public DateTimeOffset? AppliedAt { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public string? ErrorCode { get; private set; }
    public long CentralEffectiveConfigurationVersion { get; private set; }
    public long DesiredConfigurationVersion { get; private set; }
}

public sealed class AgentHeartbeatReceipt
{
    private AgentHeartbeatReceipt() { }
    public AgentHeartbeatReceipt(Guid agentId, Guid heartbeatId, DateTimeOffset receivedAt, string responseJson)
    { AgentId = agentId; HeartbeatId = heartbeatId; ReceivedAt = receivedAt; ResponseJson = Guard.Required(responseJson, nameof(responseJson), 4096); }
    public Guid AgentId { get; private set; }
    public Guid HeartbeatId { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public string ResponseJson { get; private set; } = string.Empty;
}
