using EePulse.Domain.Common;

namespace EePulse.Domain.Auditing;

public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public AuditEvent(
        Guid id,
        Guid? actorId,
        string action,
        string entityType,
        Guid? entityId,
        string? beforeJson,
        string? afterJson,
        string correlationId,
        DateTimeOffset occurredAt,
        string? sourceIp)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException(nameof(id), "Audit event id is required.") : id;
        ActorId = actorId;
        Action = Guard.Required(action, nameof(action), 100);
        EntityType = Guard.Required(entityType, nameof(entityType), 100);
        EntityId = entityId;
        BeforeJson = Guard.Optional(beforeJson, nameof(beforeJson), 1_000_000);
        AfterJson = Guard.Optional(afterJson, nameof(afterJson), 1_000_000);
        CorrelationId = Guard.Required(correlationId, nameof(correlationId), 128);
        OccurredAt = Guard.Utc(occurredAt, nameof(occurredAt));
        SourceIp = sourceIp is null ? null : Guard.IpAddress(sourceIp, nameof(sourceIp));
    }

    public Guid Id { get; private set; }
    public Guid? ActorId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public Guid? EntityId { get; private set; }
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public string? SourceIp { get; private set; }
}
