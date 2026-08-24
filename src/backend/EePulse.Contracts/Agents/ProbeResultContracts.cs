namespace EePulse.Contracts.Agents;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ProbeResultIngestionBatchRequest(
    Guid BatchId,
    IReadOnlyList<ProbeResultIngestionEnvelope> Results);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ProbeResultIngestionEnvelope(
    int ResultSchemaVersion,
    Guid ResultId,
    Guid AgentId,
    Guid ProbeId,
    long ConfigurationVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int AttemptCount,
    int SuccessfulAttemptCount,
    decimal PacketLossRatio,
    decimal? MinRttMilliseconds,
    decimal? AverageRttMilliseconds,
    decimal? MaxRttMilliseconds,
    string? ErrorCategory);

public sealed record ProbeResultIngestionBatchResponse(
    Guid BatchId,
    IReadOnlyList<Guid> AcceptedResultIds,
    IReadOnlyList<RejectedProbeResultIngestion> Rejections);

public sealed record RejectedProbeResultIngestion(Guid ResultId, string Code);

public sealed record ProbeResultBatchRequest(
    int SchemaVersion,
    Guid AgentId,
    Guid BatchId,
    DateTimeOffset CreatedAt,
    long ConfigurationVersion,
    IReadOnlyList<ProbeResultItem> Results);

public sealed record ProbeResultItem(
    Guid RunId,
    Guid ProbeId,
    DateTimeOffset ScheduledAt,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Success,
    int Attempts,
    int SuccessfulAttempts,
    double PacketLossRatio,
    double? MinRttMs,
    double? AvgRttMs,
    double? MaxRttMs,
    string? ErrorCategory,
    string? ErrorCode);

public sealed record ProbeResultBatchResponse(
    int SchemaVersion,
    Guid BatchId,
    int Accepted,
    int Duplicates,
    int Rejected,
    IReadOnlyList<RejectedProbeResult> Rejections);

public sealed record RejectedProbeResult(Guid RunId, string Code, string Message);
