using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using EePulse.Contracts.Agents;

namespace EePulse.Contracts.Dashboard;

// Phase 1 defines additive wire shapes and validation metadata only. No endpoint, hub, persistence, or domain behavior is introduced here.
public static class Wp07DashboardContract
{
    public const int SchemaVersion = 1;
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;
    public const int MaximumCursorLength = 2048;
    public const int MaximumFilterTextLength = 128;
    public const int MaximumCommentLength = 2000;
    public const int MaximumNoteLength = 2000;
    public const int MaximumMetricsPointCount = 10000;
    public const int DisconnectedRefreshSeconds = 30;
    public const int ConnectedSafetyRefreshSeconds = 60;
    public const string HubPath = "/hubs/dashboard";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ManualResolutionStateConflict = "incident-manual-resolution-state-conflict";
    public const string ActionStateConflict = "incident-action-state-conflict";
    public const string IdempotencyKeyReuseConflict = "idempotency-key-reuse-conflict";
    public const string TimezonePreferencePath = "/api/v1/users/me/timezone-preference";
}

public static class DashboardAuthorization
{
    public const string DashboardReadPolicy = "dashboard.read";
    public const string IncidentsReadPolicy = "incidents.read";
    public const string IncidentsOperatePolicy = "incidents.operate";
    public const string AuditReadPolicy = "audit.read";
    public static readonly IReadOnlyList<string> IncidentOperateRoles = ["Operator", "Administrator"];
    public static readonly IReadOnlyList<string> AuditReadRoles = ["Auditor", "Administrator"];
    public static readonly IReadOnlyList<string> InventoryMutationRoles = ["Engineer", "Administrator"];
}

public static class ManualResolutionConflictContract
{
    public const int StatusCode = 409;
    public static readonly IReadOnlyList<string> RequiredExtensions =
        ["incidentId", "probeId", "underlyingStatus", "visibleStatus", "stateVersion", "currentEtag"];
    public const string NoMutationGuarantee = "No incident, lifecycle, comment, or audit mutation is made.";
}

public static class TimezonePreferenceContract
{
    public static readonly IReadOnlyList<string> DisplayPrecedence = ["persistedOverride", "selectedSite", "browser"];
    // HTTP-visible ASCII subset: excludes SP, DQUOTE, DEL, and all controls.
    public const string StrongEtagPattern = "^\\\"[\\x21\\x23-\\x7E]+\\\"$";
}

public enum DashboardProbeStatus { Unknown, Up, Degraded, Down, Recovering, Maintenance, Disabled }
public enum IncidentStatus { Open, Acknowledged, Resolved }
public enum IncidentSort { OpenedAtDesc, OpenedAtAsc }
public enum TimelineSort { OccurredAtDesc, OccurredAtAsc }
public enum AuditSort { OccurredAtDesc, OccurredAtAsc }
public enum ChartResolution { Auto, Raw, Minute, FiveMinutes, Hour }

public sealed record DashboardFilter(
    [property: CanonicalUuid] string? SiteId,
    [property: StringLength(Wp07DashboardContract.MaximumFilterTextLength, MinimumLength = 1)] string? Area,
    [property: StringLength(Wp07DashboardContract.MaximumFilterTextLength, MinimumLength = 1)] string? DeviceType,
    [property: StringLength(Wp07DashboardContract.MaximumFilterTextLength, MinimumLength = 1)] string? Criticality,
    [property: StringLength(Wp07DashboardContract.MaximumFilterTextLength, MinimumLength = 1)] string? Tag,
    [property: EnumDataType(typeof(DashboardProbeStatus))] DashboardProbeStatus? Status);

public sealed record DashboardStatusCount(DashboardProbeStatus Status, long Count);
public sealed record DashboardRecentDownItem([property: Required, CanonicalUuid] string DeviceId, string DeviceName, [property: Required, CanonicalUuid] string ProbeId, [property: Required, CanonicalUuid] string SiteId, string SiteName, string? Area, string Criticality, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset SinceAt, [property: CanonicalUuid] string? OpenIncidentId);
public sealed record DashboardOfflineAgentItem([property: Required, CanonicalUuid] string AgentId, string AgentName, [property: Required, CanonicalUuid] string AgentGroupId, string Status, string SelfHealth, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? LastHeartbeatAt, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? LastReportedAt);
public sealed record DashboardOpenIncidentItem([property: Required, CanonicalUuid] string IncidentId, [property: Required, CanonicalUuid] string DeviceId, string DeviceName, [property: Required, CanonicalUuid] string ProbeId, [property: Required, CanonicalUuid] string SiteId, string SiteName, IncidentStatus Status, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset OpenedAt, int OccurrenceCount, string Criticality);
public sealed record DashboardSummaryResponse(int SchemaVersion, DashboardFilter AppliedFilter, IReadOnlyList<DashboardStatusCount> StatusCounts, IReadOnlyList<DashboardRecentDownItem> RecentlyDown, IReadOnlyList<DashboardOfflineAgentItem> OfflineAgents, IReadOnlyList<DashboardOpenIncidentItem> OpenIncidents, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset GeneratedAt);

public sealed record DeviceStatusResponse([property: Required, CanonicalUuid] string ProbeId, DashboardProbeStatus VisibleStatus, DashboardProbeStatus UnderlyingStatus, long StateVersion, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? LastFreshEventAt, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? LastReceivedAt, [property: CanonicalUuid] string? AgentId, string? AgentName, [property: CanonicalUuid] string? OpenIncidentId);
public sealed record StatusEnrichedDeviceResponse([property: Required, CanonicalUuid] string Id, [property: Required, CanonicalUuid] string SiteId, string Name, string Address, string? Hostname, string DeviceType, string? Area, string? Owner, string Criticality, string[] Tags, bool Enabled, long RowVersion, IReadOnlyList<DeviceStatusResponse> Probes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MetricsQuery([property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset From, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset To, [property: EnumDataType(typeof(ChartResolution))] ChartResolution Resolution, [property: Range(1, Wp07DashboardContract.MaximumMetricsPointCount)] int PointCount = Wp07DashboardContract.MaximumMetricsPointCount) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From.Offset != TimeSpan.Zero || To.Offset != TimeSpan.Zero) yield return new("Metrics bounds must be UTC.", [nameof(From), nameof(To)]);
        if (From >= To || To - From > TimeSpan.FromDays(31)) yield return new("Metrics range must be positive and no longer than 31 days.", [nameof(From), nameof(To)]);
    }
}

public sealed record MetricPoint([property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset At, decimal? AverageRttMilliseconds, decimal PacketLossRatio, int AttemptCount, int SuccessfulAttemptCount);
public sealed record MetricsSeries([property: Required, CanonicalUuid] string ProbeId, ChartResolution EffectiveResolution, IReadOnlyList<MetricPoint> Points, bool IsPartial, string? PartialErrorCode);
public sealed record DeviceMetricsResponse(int SchemaVersion, [property: Required, CanonicalUuid] string DeviceId, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset From, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset To, ChartResolution RequestedResolution, IReadOnlyList<MetricsSeries> Series);
public sealed record DeviceTimelineItem([property: Required, CanonicalUuid] string TransitionId, [property: Required, CanonicalUuid] string ProbeId, DashboardProbeStatus FromStatus, DashboardProbeStatus ToStatus, string ReasonCode, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset OccurredAt, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset ReceivedAt, long StateVersion);
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, [property: Range(1, Wp07DashboardContract.MaximumPageSize)] int PageSize);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CursorPageRequest([property: StringLength(Wp07DashboardContract.MaximumCursorLength, MinimumLength = 1)] string? Cursor, [property: Range(1, Wp07DashboardContract.MaximumPageSize)] int PageSize = Wp07DashboardContract.DefaultPageSize);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IncidentListFilter([property: CanonicalUuid] string? SiteId, [property: CanonicalUuid] string? DeviceId, [property: CanonicalUuid] string? ProbeId, [property: EnumDataType(typeof(IncidentStatus))] IncidentStatus? Status, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? OpenedFrom, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? OpenedTo, [property: EnumDataType(typeof(IncidentSort))] IncidentSort Sort) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OpenedFrom.HasValue && OpenedTo.HasValue && OpenedFrom > OpenedTo) yield return new("OpenedFrom must not be after OpenedTo.", [nameof(OpenedFrom), nameof(OpenedTo)]);
    }
}

public sealed record IncidentResponse([property: Required, CanonicalUuid] string Id, [property: Required, CanonicalUuid] string ProbeId, [property: Required, CanonicalUuid] string DeviceId, string DeviceName, [property: Required, CanonicalUuid] string SiteId, string SiteName, string RuleKey, IncidentStatus Status, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset OpenedAt, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? AcknowledgedAt, [property: CanonicalUuid] string? AcknowledgedBy, string? AcknowledgementComment, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? ResolvedAt, [property: CanonicalUuid] string? ResolvedBy, string? ResolutionNote, int OccurrenceCount, TimeSpan? TotalDowntime, long RowVersion);
public sealed record IncidentLifecycleResponse([property: Required, CanonicalUuid] string EventId, [property: Required, CanonicalUuid] string IncidentId, string Type, string ReasonCode, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset OccurredAt, [property: CanonicalUuid] string? ActorId, string? Comment);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record AcknowledgeIncidentRequest([property: Required, StringLength(Wp07DashboardContract.MaximumCommentLength, MinimumLength = 1)] string Comment);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record ResolveIncidentRequest([property: Required, StringLength(Wp07DashboardContract.MaximumNoteLength, MinimumLength = 1)] string Note);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record AddIncidentCommentRequest([property: Required, StringLength(Wp07DashboardContract.MaximumCommentLength, MinimumLength = 1)] string Comment);
public sealed record IncidentActionResponse([property: Required, CanonicalUuid] string IncidentId, IncidentStatus Status, long RowVersion, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset CompletedAt);
public sealed record IncidentCommentResponse([property: Required, CanonicalUuid] string Id, [property: Required, CanonicalUuid] string IncidentId, [property: Required, CanonicalUuid] string AuthorId, string Comment, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset CreatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuditLogFilter([property: StringLength(Wp07DashboardContract.MaximumFilterTextLength, MinimumLength = 1)] string? Action, [property: StringLength(Wp07DashboardContract.MaximumFilterTextLength, MinimumLength = 1)] string? EntityType, [property: CanonicalUuid] string? EntityId, [property: CanonicalUuid] string? ActorId, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? From, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? To, [property: EnumDataType(typeof(AuditSort))] AuditSort Sort) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From.HasValue && To.HasValue && From > To) yield return new("From must not be after To.", [nameof(From), nameof(To)]);
    }
}

public sealed record AuditLogEntryResponse([property: Required, CanonicalUuid] string Id, [property: CanonicalUuid] string? ActorId, string Action, string EntityType, [property: CanonicalUuid] string? EntityId, [property: Required, CanonicalUuid] string CorrelationId, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset OccurredAt, string? SourceIp);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record TimezonePreferenceRequest([property: IanaTimeZone] string? Timezone);
public sealed record TimezonePreferenceResponse(string? Timezone, [property: Required, RegularExpression(TimezonePreferenceContract.StrongEtagPattern)] string Etag);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DashboardInvalidationEvent([property: Range(1, 1)] int SchemaVersion, [property: Required, StringLength(64, MinimumLength = 1)] string EventType, [property: Required, CanonicalUuid] string EntityId, [property: CanonicalUuid] string? DeviceId, [property: CanonicalUuid] string? ProbeId, [property: CanonicalUuid] string? SiteId, [property: Range(typeof(long), "1", "9223372036854775807")] long? Version, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset OccurredAt) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EventType is not ("status.changed" or "incident.changed" or "agent.changed" or "maintenance.changed")) yield return new("EventType is unsupported.", [nameof(EventType)]);
    }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class IanaTimeZoneAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is null) return true; // Null clears the override.
        if (value is not string timezone || timezone.Length is 0 or > 255 || !timezone.Contains('/')) return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timezone); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class CanonicalUuidAttribute : ValidationAttribute
{
    public const string Pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z";
    private static readonly Regex CanonicalUuid = new(Pattern, RegexOptions.CultureInvariant);

    public override bool IsValid(object? value) => value is null || value is string identifier && CanonicalUuid.IsMatch(identifier);
}
