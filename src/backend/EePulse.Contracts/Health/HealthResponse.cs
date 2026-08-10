namespace EePulse.Contracts.Health;

public sealed record HealthResponse(
    int SchemaVersion,
    string Service,
    string Status,
    DateTimeOffset CheckedAt,
    string Version);
