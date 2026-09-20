namespace EePulse.Domain.Status;

public sealed class IncidentComment
{
    private IncidentComment() { }

    public IncidentComment(Guid id, Guid incidentId, Guid probeId, Guid authorId, string comment,
        DateTimeOffset createdAt, string idempotencyKey)
    {
        Id = id;
        IncidentId = incidentId;
        ProbeId = probeId;
        AuthorId = authorId;
        Comment = comment;
        CreatedAt = createdAt;
        IdempotencyKey = idempotencyKey;
    }

    public Guid Id { get; private set; }
    public Guid IncidentId { get; private set; }
    public Guid ProbeId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Comment { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
}

public sealed class IncidentLifecycleAction
{
    private IncidentLifecycleAction() { }

    public IncidentLifecycleAction(Guid eventId, Guid incidentId, Guid probeId, Guid actorId,
        string actionKind, string reasonCode, DateTimeOffset occurredAt, string actionNote,
        string idempotencyKey)
    {
        EventId = eventId;
        IncidentId = incidentId;
        ProbeId = probeId;
        ActorId = actorId;
        ActionKind = actionKind;
        ReasonCode = reasonCode;
        OccurredAt = occurredAt;
        ActionNote = actionNote;
        IdempotencyKey = idempotencyKey;
    }

    public Guid EventId { get; private set; }
    public Guid IncidentId { get; private set; }
    public Guid ProbeId { get; private set; }
    public Guid ActorId { get; private set; }
    public string ActionKind { get; private set; } = string.Empty;
    public string ReasonCode { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public string ActionNote { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
}

public sealed class IdempotencyReceipt
{
    private IdempotencyReceipt() { }

    public IdempotencyReceipt(string idempotencyKey, string routeTemplate, Guid incidentId, Guid actorId,
        short requestFingerprintVersion, byte[] requestDigest, int responseStatus, byte[] responseBody,
        string responseContentType, string responseEtag, string outcomeKind, Guid? incidentCommentId,
        Guid? incidentLifecycleActionId, DateTimeOffset createdAt, DateTimeOffset completedAt)
    {
        IdempotencyKey = idempotencyKey;
        RouteTemplate = routeTemplate;
        IncidentId = incidentId;
        ActorId = actorId;
        RequestFingerprintVersion = requestFingerprintVersion;
        RequestDigest = requestDigest;
        ResponseStatus = responseStatus;
        ResponseBody = responseBody;
        ResponseContentType = responseContentType;
        ResponseEtag = responseEtag;
        OutcomeKind = outcomeKind;
        IncidentCommentId = incidentCommentId;
        IncidentLifecycleActionId = incidentLifecycleActionId;
        CreatedAt = createdAt;
        CompletedAt = completedAt;
    }

    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RouteTemplate { get; private set; } = string.Empty;
    public Guid IncidentId { get; private set; }
    public Guid ActorId { get; private set; }
    public short RequestFingerprintVersion { get; private set; }
    public byte[] RequestDigest { get; private set; } = [];
    public int ResponseStatus { get; private set; }
    public byte[] ResponseBody { get; private set; } = [];
    public string ResponseContentType { get; private set; } = string.Empty;
    public string ResponseEtag { get; private set; } = string.Empty;
    public string OutcomeKind { get; private set; } = string.Empty;
    public Guid? IncidentCommentId { get; private set; }
    public Guid? IncidentLifecycleActionId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset CompletedAt { get; private set; }
}
