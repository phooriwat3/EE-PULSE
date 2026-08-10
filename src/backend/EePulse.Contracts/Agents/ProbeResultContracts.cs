namespace EePulse.Contracts.Agents;

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
