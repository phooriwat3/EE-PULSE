using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EePulse.Contracts.Agents;

public static class AgentContract
{
    public const int SchemaVersion = 1;
    public const string CredentialAuthenticationScheme = "AgentCredential";
    public const string CredentialBearerFormat = "EE-Pulse-Agent-v1";
}

public static class AgentProblemCodes
{
    public const string RequestInvalid = "request-invalid";
    public const string SchemaUnsupported = "schema-unsupported";
    public const string TimestampNotUtc = "timestamp-not-utc";
    public const string AgentAuthenticationRequired = "agent-authentication-required";
    public const string EnrollmentTokenInvalid = "enrollment-token-invalid";
    public const string AgentIdentityMismatch = "agent-identity-mismatch";
    public const string NetworkPolicyMismatch = "network-policy-mismatch";
    public const string AgentNotFound = "agent-not-found";
    public const string ConfigurationNotFound = "configuration-not-found";
    public const string ConfigurationConflict = "configuration-conflict";
    public const string AcknowledgementConflict = "acknowledgement-conflict";
    public const string AgentGroupDisabled = "agent-group-disabled";
    public const string EnrollmentTokenUnavailable = "enrollment-token-unavailable";
    public const string AgentRevoked = "agent-revoked";
    public const string ConfigurationRetired = "configuration-retired";
    public const string AgentVersionUnsupported = "agent-version-unsupported";
    public const string RateLimitExceeded = "rate-limit-exceeded";
    public const string ServerError = "server-error";
    public const string DependencyUnavailable = "dependency-unavailable";
}

public static class AgentConfigurationRejectionCodes
{
    public const string SchemaUnsupported = "schema-unsupported";
    public const string NetworkPolicyMismatch = "network-policy-mismatch";
    public const string ConfigurationInvalid = "configuration-invalid";
    public const string ConfigurationStorageFailed = "configuration-storage-failed";
    public const string SchedulerApplyFailed = "scheduler-apply-failed";

    public static bool Contains(string? value) => value is
        SchemaUnsupported or
        NetworkPolicyMismatch or
        ConfigurationInvalid or
        ConfigurationStorageFailed or
        SchedulerApplyFailed;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAgentEnrollmentTokenRequest(
    [property: Range(1, 1)] int SchemaVersion,
    Guid AgentGroupId,
    [property: StringLength(200, MinimumLength = 1)] string Label,
    [property: StringLength(255, MinimumLength = 1)] string? ExpectedMachineName,
    [property: MinLength(1), MaxLength(64)] IReadOnlyList<string> AllowedNetworks,
    [property: Range(60, 86400)] int ExpiresInSeconds = 900);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAgentEnrollmentTokenResponse(
    int SchemaVersion,
    Guid TokenId,
    [property: StringLength(256, MinimumLength = 48)] string EnrollmentToken,
    Guid AgentGroupId,
    IReadOnlyList<string> AllowedNetworks,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentEnrollmentRequest(
    [property: Range(1, 1)] int SchemaVersion,
    [property: StringLength(256, MinimumLength = 48)] string EnrollmentToken,
    Guid ClientInstanceId,
    [property: StringLength(255, MinimumLength = 1)] string MachineName,
    [property: StringLength(64, MinimumLength = 1)] string AgentVersion,
    [property: MinLength(1), MaxLength(64)] IReadOnlyList<string> LocalAllowedNetworks,
    DateTimeOffset SentAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentEnrollmentResponse(
    int SchemaVersion,
    Guid AgentId,
    Guid AgentGroupId,
    Guid CredentialId,
    [property: StringLength(256, MinimumLength = 48)] string AgentCredential,
    DateTimeOffset CredentialExpiresAt,
    DateTimeOffset RotateAfter,
    DateTimeOffset ServerTime,
    int HeartbeatIntervalSeconds,
    int HeartbeatExpiresAfterSeconds,
    long DesiredConfigurationVersion,
    string ConfigurationUrl);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentResponse(
    int SchemaVersion,
    Guid Id,
    Guid AgentGroupId,
    string Name,
    string MachineName,
    string AgentVersion,
    string Status,
    string SelfHealth,
    long QueueDepth,
    IReadOnlyList<string> AllowedNetworks,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastReportedAt,
    long DesiredConfigurationVersion,
    long LastAppliedConfigurationVersion,
    DateTimeOffset? LastConfigurationAcknowledgedAt,
    bool ClockSkewSuspected,
    DateTimeOffset? CredentialExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PagedAgentResponse(
    IReadOnlyList<AgentResponse> Items,
    int Page,
    int PageSize,
    long TotalCount);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentHeartbeatRequest(
    [property: Range(1, 1)] int SchemaVersion,
    Guid HeartbeatId,
    [property: StringLength(64, MinimumLength = 1)] string AgentVersion,
    [property: StringLength(255, MinimumLength = 1)] string MachineName,
    [property: Range(typeof(long), "0", "9223372036854775807")] long CurrentConfigurationVersion,
    [property: Range(typeof(long), "0", "9223372036854775807")] long QueueDepth,
    string HealthState,
    DateTimeOffset SentAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentHeartbeatResponse(
    int SchemaVersion,
    Guid HeartbeatId,
    Guid AgentId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ServerTime,
    int NextHeartbeatSeconds,
    long DesiredConfigurationVersion,
    bool ConfigurationChanged,
    bool CredentialRotationRequired,
    bool ClockSkewSuspected,
    string? WarningCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentConfigurationResponse(
    int SchemaVersion,
    Guid AgentId,
    Guid AgentGroupId,
    long ConfigurationVersion,
    DateTimeOffset GeneratedAt,
    long? RollbackOfVersion,
    IReadOnlyList<string> AllowedNetworks,
    IReadOnlyList<AgentProbeConfiguration> Probes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentProbeConfiguration(
    Guid ProbeId,
    Guid DeviceId,
    long ProbeConfigVersion,
    string Type,
    string TargetAddress,
    int IntervalSeconds,
    int TimeoutMilliseconds,
    int AttemptCount,
    int? WarningRttMilliseconds,
    int? CriticalRttMilliseconds,
    int FailureThreshold,
    int RecoveryThreshold);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentConfigurationAcknowledgementRequest(
    [property: Range(1, 1)] int SchemaVersion,
    Guid AcknowledgementId,
    [property: Range(typeof(long), "1", "9223372036854775807")] long ConfigurationVersion,
    string Status,
    DateTimeOffset? AppliedAt,
    [property: StringLength(100)] string? ErrorCode,
    DateTimeOffset SentAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentConfigurationAcknowledgementResponse(
    int SchemaVersion,
    Guid AcknowledgementId,
    Guid AgentId,
    long ConfigurationVersion,
    DateTimeOffset AcceptedAt,
    long CentralEffectiveConfigurationVersion,
    long DesiredConfigurationVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RotateAgentCredentialRequest([property: Range(1, 1)] int SchemaVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RotateAgentCredentialResponse(
    int SchemaVersion,
    Guid CredentialId,
    [property: StringLength(256, MinimumLength = 48)] string AgentCredential,
    DateTimeOffset ExpiresAt,
    DateTimeOffset RotateAfter);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RevokeAgentRequest(
    [property: Range(1, 1)] int SchemaVersion,
    string ReasonCode,
    long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAgentAllowedNetworksRequest(
    [property: Range(1, 1)] int SchemaVersion,
    [property: MinLength(1), MaxLength(64)] IReadOnlyList<string> AllowedNetworks,
    long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAgentGroupAllowedNetworksRequest(
    [property: Range(1, 1)] int SchemaVersion,
    [property: MinLength(1), MaxLength(64)] IReadOnlyList<string> AllowedNetworks,
    long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentNetworkPolicyResponse(
    int SchemaVersion,
    Guid OwnerId,
    IReadOnlyList<string> AllowedNetworks,
    long ConfigurationVersion,
    long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RollbackAgentConfigurationRequest(
    [property: Range(1, 1)] int SchemaVersion,
    [property: Range(typeof(long), "1", "9223372036854775807")] long SourceConfigurationVersion,
    long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentConfigurationPublicationResponse(
    int SchemaVersion,
    Guid AgentGroupId,
    long ConfigurationVersion,
    long? RollbackOfVersion,
    DateTimeOffset GeneratedAt);
