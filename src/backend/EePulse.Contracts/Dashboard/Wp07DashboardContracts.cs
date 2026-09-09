using System.ComponentModel.DataAnnotations;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using EePulse.Contracts.Agents;
using NodaTime.TimeZones;

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

public static partial class TimezonePreferenceContract
{
    public static readonly IReadOnlyList<string> DisplayPrecedence = ["persistedOverride", "selectedSite", "browser"];
    // HTTP-visible ASCII subset: excludes SP, DQUOTE, DEL, and all controls.
    public const string StrongEtagPattern = "^\\\"[\\x21\\x23-\\x7E]+\\\"$";
    public const string NoPersistedRowEtag = "\"tz-0\"";
    public const string PersistedEtagPrefix = "\"tz-";
    public const string PersistedEtagSuffix = "\"";
    public const string InvalidIfMatchCode = "invalid-if-match";
    public const string ConcurrencyConflictCode = "concurrency-conflict";
    public const int MaximumIssuerLength = 512;
    public const int MaximumSubjectLength = 512;

    // NodaTime 3.3.3 embeds TZDB. UTC normalizes to the provider's canonical Etc/UTC ID.
    public const string CanonicalUtcId = "Etc/UTC";

    public static string EtagForVersion(long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return version == 0 ? NoPersistedRowEtag : $"\"tz-{version}\"";
    }

    public static bool IsStrongEtag(string? etag) => etag is not null && StrongEtag().IsMatch(etag);

    public static TimezoneIfMatchClassification ClassifyIfMatch(IEnumerable<string>? values, string currentEtag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentEtag);
        if (!IsStrongEtag(currentEtag)) throw new ArgumentException("The current ETag must be strong.", nameof(currentEtag));
        if (!TryGetIfMatchVersion(values, out var version, out var classification)) return classification;
        return string.Equals(EtagForVersion(version), currentEtag, StringComparison.Ordinal)
            ? TimezoneIfMatchClassification.Current
            : TimezoneIfMatchClassification.Stale;
    }

    public static bool TryGetIfMatchVersion(IEnumerable<string>? values, out long version, out TimezoneIfMatchClassification classification)
    {
        version = 0;
        var candidates = values?.ToArray() ?? [];
        if (candidates.Length == 0) { classification = TimezoneIfMatchClassification.Missing; return false; }
        if (candidates.Length != 1 || !TryParseEntityTagList(candidates, out var tags) || tags.Count != 1 || tags[0].Weak ||
            !TryParseTimezoneEtag(tags[0].Tag, out version))
        {
            classification = TimezoneIfMatchClassification.Invalid;
            return false;
        }
        classification = TimezoneIfMatchClassification.Current;
        return true;
    }

    public static TimezoneIfNoneMatchClassification ClassifyIfNoneMatch(
        IEnumerable<string>? values, string currentEtag, bool rowExists)
    {
        var candidates = values?.ToArray() ?? [];
        if (candidates.Length == 0) return TimezoneIfNoneMatchClassification.Missing;
        if (!TryParseEntityTagList(candidates, out var tags)) return TimezoneIfNoneMatchClassification.Invalid;
        if (tags.Count == 1 && tags[0].Wildcard) return rowExists ? TimezoneIfNoneMatchClassification.Match : TimezoneIfNoneMatchClassification.NoMatch;
        if (tags.Any(tag => tag.Wildcard)) return TimezoneIfNoneMatchClassification.Invalid;
        if (tags.Any(tag => tag.Tag.StartsWith("\"tz-", StringComparison.Ordinal) && !TryParseTimezoneEtag(tag.Tag, out _)))
            return TimezoneIfNoneMatchClassification.Invalid;
        return tags.Any(tag => string.Equals(tag.Tag, currentEtag, StringComparison.Ordinal))
            ? TimezoneIfNoneMatchClassification.Match : TimezoneIfNoneMatchClassification.NoMatch;
    }

    private static bool TryParseTimezoneEtag(string value, out long version)
    {
        version = 0;
        if (string.Equals(value, NoPersistedRowEtag, StringComparison.Ordinal)) return true;
        const string prefix = "\"tz-";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith('"') || value.Length <= prefix.Length + 1) return false;
        var digits = value[prefix.Length..^1];
        return digits[0] is not '0' && digits.All(character => character is >= '0' and <= '9') &&
            long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1;
    }

    private static bool TryParseEntityTagList(IEnumerable<string> values, out List<ParsedEntityTag> tags)
    {
        tags = [];
        foreach (var value in values)
        {
            if (value is null) return false;
            var index = 0;
            var expectTag = true;
            while (true)
            {
                while (index < value.Length && value[index] is ' ' or '\t') index++;
                if (index == value.Length) { if (expectTag) return false; break; }
                if (!expectTag) return false;
                if (value[index] == '*')
                {
                    tags.Add(new ParsedEntityTag(false, true, string.Empty)); index++;
                }
                else
                {
                    var weak = value.AsSpan(index).StartsWith("W/", StringComparison.Ordinal);
                    if (weak) index += 2;
                    if (index >= value.Length || value[index++] != '"') return false;
                    var start = index;
                    while (index < value.Length && value[index] != '"')
                    {
                        var character = value[index++];
                        if (character < '\x21' || character == '"' || character > '\x7e') return false;
                    }
                    if (index == start || index >= value.Length) return false;
                    var tag = "\"" + value[start..index] + "\"";
                    index++;
                    tags.Add(new ParsedEntityTag(weak, false, tag));
                }
                while (index < value.Length && value[index] is ' ' or '\t') index++;
                if (index == value.Length) break;
                if (value[index++] != ',') return false;
                expectTag = true;
            }
        }
        return tags.Count != 0;
    }

    private sealed record ParsedEntityTag(bool Weak, bool Wildcard, string Tag);

    public static bool TryNormalizeTimezone(string? timezone, out string? canonicalTimezone)
    {
        canonicalTimezone = null;
        if (timezone is null) return true;
        if (string.IsNullOrWhiteSpace(timezone) || timezone.Length > 255 || timezone.Contains('\\') ||
            timezone is "." or "..") return false;

        // CanonicalIdMap contains every embedded TZDB ID, including aliases, and is ordinal/case-sensitive.
        if (!TzdbDateTimeZoneSource.Default.CanonicalIdMap.TryGetValue(timezone, out var canonical)) return false;
        canonicalTimezone = canonical;
        return true;
    }

    public static TimezonePreferenceMutationOutcome ClassifyMutation(
        bool rowExists, string? currentTimezone, string? requestedTimezone)
    {
        if (!rowExists && currentTimezone is not null) throw new ArgumentException("An absent row cannot have a timezone.", nameof(currentTimezone));
        if (!TryNormalizeTimezone(currentTimezone, out var canonicalCurrent) ||
            !TryNormalizeTimezone(requestedTimezone, out var canonicalRequested))
            throw new ArgumentException("Timezone values must be null or recognized TZDB IDs.");
        if (!rowExists && canonicalRequested is null) return TimezonePreferenceMutationOutcome.NoOpAbsent;
        if (rowExists && string.Equals(canonicalCurrent, canonicalRequested, StringComparison.Ordinal))
            return TimezonePreferenceMutationOutcome.NoOpCurrent;
        return !rowExists ? TimezonePreferenceMutationOutcome.Create :
            canonicalRequested is null ? TimezonePreferenceMutationOutcome.Clear : TimezonePreferenceMutationOutcome.Update;
    }

    public static TimezonePreferenceState Advance(TimezonePreferenceState current, string? requestedTimezone)
    {
        ArgumentNullException.ThrowIfNull(current);
        var outcome = ClassifyMutation(current.RowExists, current.Timezone, requestedTimezone);
        if (outcome is TimezonePreferenceMutationOutcome.NoOpAbsent or TimezonePreferenceMutationOutcome.NoOpCurrent)
            return current;

        _ = TryNormalizeTimezone(requestedTimezone, out var canonicalRequested);
        return outcome == TimezonePreferenceMutationOutcome.Create
            ? new TimezonePreferenceState(true, canonicalRequested, 1)
            : new TimezonePreferenceState(true, canonicalRequested, checked(current.Version + 1));
    }

    public static bool IsValidPrincipalComponent(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) && HasSafePrincipalCharacters(value);

    private static bool HasSafePrincipalCharacters(string value)
    {
        for (var offset = 0; offset < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(offset), out var rune, out var consumed) != OperationStatus.Done) return false;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            offset += consumed;
        }
        return true;
    }

    private static Regex StrongEtag() => StrongEtagRegex();

    [GeneratedRegex(StrongEtagPattern, RegexOptions.CultureInvariant)]
    private static partial Regex StrongEtagRegex();
}

public enum TimezoneIfMatchClassification { Missing, Invalid, Current, Stale }
public enum TimezoneIfNoneMatchClassification { Missing, NoMatch, Match, Invalid }
public enum TimezonePreferenceMutationOutcome { NoOpAbsent, NoOpCurrent, Create, Update, Clear }

// A persistence-independent state machine for the frozen GET/PUT bootstrap and version policy.
public sealed record TimezonePreferenceState
{
    public TimezonePreferenceState(bool rowExists, string? timezone, long version)
    {
        if (!rowExists && (timezone is not null || version != 0))
            throw new ArgumentException("An absent preference has null timezone and version zero.");
        if (rowExists && version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), "A retained preference row has a positive version.");
        if (!TimezonePreferenceContract.TryNormalizeTimezone(timezone, out var canonicalTimezone))
            throw new ArgumentException("Timezone must be null or a recognized TZDB ID.", nameof(timezone));
        if (timezone is not null && !string.Equals(timezone, canonicalTimezone, StringComparison.Ordinal))
            throw new ArgumentException("A retained preference stores only the canonical TZDB ID.", nameof(timezone));

        RowExists = rowExists;
        Timezone = canonicalTimezone;
        Version = version;
    }

    public static TimezonePreferenceState NoPersistedRow { get; } = new(false, null, 0);
    public bool RowExists { get; }
    public string? Timezone { get; }
    public long Version { get; }
    public string Etag => TimezonePreferenceContract.EtagForVersion(Version);
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record TimezonePreferenceRequest([property: IanaTimeZone, StringLength(255)] string? Timezone);
public sealed record TimezonePreferenceResponse([property: StringLength(255)] string? Timezone, [property: Required, RegularExpression(TimezonePreferenceContract.StrongEtagPattern)] string Etag);

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
    public override bool IsValid(object? value) => value is null || value is string timezone &&
        TimezonePreferenceContract.TryNormalizeTimezone(timezone, out _);
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class CanonicalUuidAttribute : ValidationAttribute
{
    public const string Pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z";
    private static readonly Regex CanonicalUuid = new(Pattern, RegexOptions.CultureInvariant);

    public override bool IsValid(object? value) => value is null || value is string identifier && CanonicalUuid.IsMatch(identifier);
}
