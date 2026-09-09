using System.ComponentModel.DataAnnotations;
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    public const string DashboardSummaryPath = "/api/v1/dashboard/summary";
    public const string DeviceStatusPathTemplate = "/api/v1/devices/{id}/status";
    public const string DashboardCacheControl = "private, max-age=0, must-revalidate";
    public const string InvalidIfNoneMatchCode = "invalid-if-none-match";
    public const string IfMatchUnsupportedCode = "if-match-unsupported";
    public const int DashboardSummaryListMaximum = 20;
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
public sealed record DashboardSummaryResponse(int SchemaVersion, DashboardFilter AppliedFilter, IReadOnlyList<DashboardStatusCount> StatusCounts, IReadOnlyList<DashboardRecentDownItem> RecentlyDown, IReadOnlyList<DashboardOfflineAgentItem> OfflineAgents, IReadOnlyList<DashboardOpenIncidentItem> OpenIncidents);

public sealed record DeviceStatusResponse([property: Required, CanonicalUuid] string ProbeId, DashboardProbeStatus VisibleStatus, DashboardProbeStatus UnderlyingStatus, long StateVersion, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? LastFreshEventAt, [property: JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))] DateTimeOffset? LastReceivedAt, [property: CanonicalUuid] string? AgentId, string? AgentName, [property: CanonicalUuid] string? OpenIncidentId);
public sealed record StatusEnrichedDeviceResponse([property: Required, CanonicalUuid] string Id, [property: Required, CanonicalUuid] string SiteId, string Name, string Address, string? Hostname, string DeviceType, string? Area, string? Owner, string Criticality, string[] Tags, bool Enabled, long RowVersion, IReadOnlyList<DeviceStatusResponse> Probes);

// Phase 2B1 deliberately owns query representation, rather than allowing future endpoint code to
// serialize these DTOs with reflection/property-order dependent settings.
public sealed class DashboardContractValidationException(string message) : ArgumentException(message)
{
    public const string Code = "wp07-dashboard-contract-invalid";
}

public static class Wp07DashboardCanonicalizer
{
    public const string VersionMarker = "wp07-2b1";
    public const string ContentType = "application/json; charset=utf-8";
    public const string EtagPrefix = "\"wp07-2b1-sha256-";
    public const string EtagSuffix = "\"";
    private static readonly DashboardProbeStatus[] StatusOrder =
        [DashboardProbeStatus.Unknown, DashboardProbeStatus.Up, DashboardProbeStatus.Degraded, DashboardProbeStatus.Down,
         DashboardProbeStatus.Recovering, DashboardProbeStatus.Maintenance, DashboardProbeStatus.Disabled];
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.Default, Indented = false, SkipValidation = false };
    public static IReadOnlyDictionary<Type, IReadOnlySet<string>> CanonicalWireProperties { get; } = new Dictionary<Type, IReadOnlySet<string>>
    {
        [typeof(DashboardSummaryResponse)] = new HashSet<string>(["SchemaVersion", "AppliedFilter", "StatusCounts", "RecentlyDown", "OfflineAgents", "OpenIncidents"], StringComparer.Ordinal),
        [typeof(DashboardFilter)] = new HashSet<string>(["SiteId", "Area", "DeviceType", "Criticality", "Tag", "Status"], StringComparer.Ordinal),
        [typeof(DashboardStatusCount)] = new HashSet<string>(["Status", "Count"], StringComparer.Ordinal),
        [typeof(DashboardRecentDownItem)] = new HashSet<string>(["DeviceId", "DeviceName", "ProbeId", "SiteId", "SiteName", "Area", "Criticality", "SinceAt", "OpenIncidentId"], StringComparer.Ordinal),
        [typeof(DashboardOfflineAgentItem)] = new HashSet<string>(["AgentId", "AgentName", "AgentGroupId", "Status", "SelfHealth", "LastHeartbeatAt", "LastReportedAt"], StringComparer.Ordinal),
        [typeof(DashboardOpenIncidentItem)] = new HashSet<string>(["IncidentId", "DeviceId", "DeviceName", "ProbeId", "SiteId", "SiteName", "Status", "OpenedAt", "OccurrenceCount", "Criticality"], StringComparer.Ordinal),
        [typeof(StatusEnrichedDeviceResponse)] = new HashSet<string>(["Id", "SiteId", "Name", "Address", "Hostname", "DeviceType", "Area", "Owner", "Criticality", "Tags", "Enabled", "RowVersion", "Probes"], StringComparer.Ordinal),
        [typeof(DeviceStatusResponse)] = new HashSet<string>(["ProbeId", "VisibleStatus", "UnderlyingStatus", "StateVersion", "LastFreshEventAt", "LastReceivedAt", "AgentId", "AgentName", "OpenIncidentId"], StringComparer.Ordinal)
    };

    public static DashboardFilter NormalizeFilter(DashboardFilter? filter)
    {
        filter ??= new DashboardFilter(null, null, null, null, null, null);
        return new DashboardFilter(NormalizeOptionalUuid(filter.SiteId, nameof(filter.SiteId)), NormalizeText(filter.Area, nameof(filter.Area)),
            NormalizeText(filter.DeviceType, nameof(filter.DeviceType)), NormalizeText(filter.Criticality, nameof(filter.Criticality)),
            NormalizeText(filter.Tag, nameof(filter.Tag)), filter.Status.HasValue ? NormalizeStatus(filter.Status.Value) : null);
    }

    public static byte[] SerializeSummary(DashboardSummaryResponse response)
    {
        var normalizedFilter = ValidateSummary(response);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("appliedFilter"); WriteFilter(writer, normalizedFilter);
            writer.WritePropertyName("offlineAgents"); WriteOfflineAgents(writer, response.OfflineAgents);
            writer.WritePropertyName("openIncidents"); WriteOpenIncidents(writer, response.OpenIncidents);
            writer.WritePropertyName("recentlyDown"); WriteRecentlyDown(writer, response.RecentlyDown);
            writer.WriteNumber("schemaVersion", response.SchemaVersion);
            writer.WritePropertyName("statusCounts"); WriteStatusCounts(writer, response.StatusCounts);
            writer.WriteEndObject();
        });
    }

    public static byte[] SerializeDeviceStatus(StatusEnrichedDeviceResponse response)
    {
        ValidateDeviceStatus(response);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("address", response.Address); writer.WriteString("area", response.Area); writer.WriteString("criticality", response.Criticality);
            writer.WriteString("deviceType", response.DeviceType); writer.WriteBoolean("enabled", response.Enabled); writer.WriteString("hostname", response.Hostname);
            writer.WriteString("id", response.Id); writer.WriteString("name", response.Name); writer.WriteString("owner", response.Owner);
            writer.WritePropertyName("probes"); writer.WriteStartArray(); foreach (var probe in response.Probes.OrderBy(probe => probe.ProbeId, StringComparer.Ordinal)) WriteDeviceStatus(writer, probe); writer.WriteEndArray();
            writer.WriteNumber("rowVersion", response.RowVersion); writer.WriteString("siteId", response.SiteId);
            writer.WritePropertyName("tags"); writer.WriteStartArray(); foreach (var tag in response.Tags.OrderBy(tag => tag, StringComparer.Ordinal)) writer.WriteStringValue(tag); writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static string EtagFor(ReadOnlySpan<byte> canonicalUtf8) => EtagPrefix + Base64Url(SHA256.HashData(canonicalUtf8)) + EtagSuffix;
    public static string EtagForSummary(DashboardSummaryResponse response) => EtagFor(SerializeSummary(response));
    public static string EtagForDeviceStatus(StatusEnrichedDeviceResponse response) => EtagFor(SerializeDeviceStatus(response));

    public static DashboardProbeStatus ResolveVisibleStatus(bool deviceEnabled, bool probeEnabled, bool activeMaintenance, DashboardProbeStatus? projectionVisibleStatus) =>
        !deviceEnabled || !probeEnabled ? DashboardProbeStatus.Disabled : activeMaintenance ? DashboardProbeStatus.Maintenance : projectionVisibleStatus.HasValue ? NormalizeStatus(projectionVisibleStatus.Value) : DashboardProbeStatus.Unknown;

    public static DeviceStatusResponse MissingProjection(string probeId) => new(NormalizeUuid(probeId, nameof(probeId)), DashboardProbeStatus.Unknown,
        DashboardProbeStatus.Unknown, 0, null, null, null, null, null);

    public static DateTimeOffset NormalizePostgresTimestamp(DateTimeOffset value) => NormalizeUtc(value).AddTicks(-(NormalizeUtc(value).Ticks % 10));

    public static DashboardFilter ValidateSummary(DashboardSummaryResponse? response)
    {
        response = response ?? throw new DashboardContractValidationException("Summary is required.");
        if (response.SchemaVersion != Wp07DashboardContract.SchemaVersion) Fail("SchemaVersion is unsupported.");
        if (response.AppliedFilter is null) throw new DashboardContractValidationException("AppliedFilter is required.");
        var normalizedFilter = NormalizeFilter(response.AppliedFilter);
        ValidateStatusCounts(response.StatusCounts); ValidateRecentDown(response.RecentlyDown); ValidateOfflineAgents(response.OfflineAgents); ValidateOpenIncidents(response.OpenIncidents);
        return normalizedFilter;
    }

    public static void ValidateDeviceStatus(StatusEnrichedDeviceResponse? response)
    {
        response = response ?? throw new DashboardContractValidationException("Device status is required.");
        RequiredUuid(response.Id, nameof(response.Id)); RequiredUuid(response.SiteId, nameof(response.SiteId)); RequiredText(response.Name, nameof(response.Name)); RequiredText(response.Address, nameof(response.Address)); RequiredText(response.DeviceType, nameof(response.DeviceType)); RequiredText(response.Criticality, nameof(response.Criticality)); OptionalText(response.Hostname, nameof(response.Hostname)); OptionalText(response.Area, nameof(response.Area)); OptionalText(response.Owner, nameof(response.Owner));
        if (response.RowVersion < 0) Fail("RowVersion is negative.");
        var responseTags = response.Tags ?? throw new DashboardContractValidationException("Tags are required.");
        var responseProbes = response.Probes ?? throw new DashboardContractValidationException("Probes are required.");
        var tags = new HashSet<string>(StringComparer.Ordinal); foreach (var tag in responseTags) { RequiredText(tag, nameof(response.Tags)); if (!tags.Add(tag)) Fail("Duplicate tag."); }
        var probes = new HashSet<string>(StringComparer.Ordinal); foreach (var probe in responseProbes) { if (probe is null) throw new DashboardContractValidationException("Null probe."); ValidateProbe(probe); if (!probes.Add(probe.ProbeId)) Fail("Duplicate ProbeId."); }
    }

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) { write(writer); }
        return buffer.WrittenSpan.ToArray();
    }
    private static void WriteFilter(Utf8JsonWriter writer, DashboardFilter filter)
    {
        writer.WriteStartObject(); writer.WriteString("area", filter.Area); writer.WriteString("criticality", filter.Criticality); writer.WriteString("deviceType", filter.DeviceType);
        writer.WriteString("siteId", filter.SiteId); if (filter.Status.HasValue) writer.WriteString("status", EnumToken(filter.Status.Value)); else writer.WriteNull("status"); writer.WriteString("tag", filter.Tag); writer.WriteEndObject();
    }
    private static void WriteStatusCounts(Utf8JsonWriter writer, IReadOnlyList<DashboardStatusCount> counts)
    {
        writer.WriteStartArray(); foreach (var count in counts) { writer.WriteStartObject(); writer.WriteNumber("count", count.Count); writer.WriteString("status", EnumToken(count.Status)); writer.WriteEndObject(); } writer.WriteEndArray();
    }
    private static void WriteRecentlyDown(Utf8JsonWriter writer, IReadOnlyList<DashboardRecentDownItem> items)
    {
        writer.WriteStartArray(); foreach (var item in items) { writer.WriteStartObject(); writer.WriteString("area", item.Area); writer.WriteString("criticality", item.Criticality); writer.WriteString("deviceId", item.DeviceId); writer.WriteString("deviceName", item.DeviceName); writer.WriteString("openIncidentId", item.OpenIncidentId); writer.WriteString("probeId", item.ProbeId); writer.WriteString("sinceAt", Timestamp(item.SinceAt)); writer.WriteString("siteId", item.SiteId); writer.WriteString("siteName", item.SiteName); writer.WriteEndObject(); } writer.WriteEndArray();
    }
    private static void WriteOfflineAgents(Utf8JsonWriter writer, IReadOnlyList<DashboardOfflineAgentItem> items)
    {
        writer.WriteStartArray(); foreach (var item in items) { writer.WriteStartObject(); writer.WriteString("agentGroupId", item.AgentGroupId); writer.WriteString("agentId", item.AgentId); writer.WriteString("agentName", item.AgentName); WriteOptionalTimestamp(writer, "lastHeartbeatAt", item.LastHeartbeatAt); WriteOptionalTimestamp(writer, "lastReportedAt", item.LastReportedAt); writer.WriteString("selfHealth", item.SelfHealth); writer.WriteString("status", item.Status); writer.WriteEndObject(); } writer.WriteEndArray();
    }
    private static void WriteOpenIncidents(Utf8JsonWriter writer, IReadOnlyList<DashboardOpenIncidentItem> items)
    {
        writer.WriteStartArray(); foreach (var item in items) { writer.WriteStartObject(); writer.WriteString("criticality", item.Criticality); writer.WriteString("deviceId", item.DeviceId); writer.WriteString("deviceName", item.DeviceName); writer.WriteString("incidentId", item.IncidentId); writer.WriteNumber("occurrenceCount", item.OccurrenceCount); writer.WriteString("openedAt", Timestamp(item.OpenedAt)); writer.WriteString("probeId", item.ProbeId); writer.WriteString("siteId", item.SiteId); writer.WriteString("siteName", item.SiteName); writer.WriteString("status", EnumToken(item.Status)); writer.WriteEndObject(); } writer.WriteEndArray();
    }
    private static void WriteDeviceStatus(Utf8JsonWriter writer, DeviceStatusResponse status)
    {
        writer.WriteStartObject(); writer.WriteString("agentId", status.AgentId); writer.WriteString("agentName", status.AgentName); WriteOptionalTimestamp(writer, "lastFreshEventAt", status.LastFreshEventAt); WriteOptionalTimestamp(writer, "lastReceivedAt", status.LastReceivedAt); writer.WriteString("openIncidentId", status.OpenIncidentId); writer.WriteString("probeId", status.ProbeId); writer.WriteNumber("stateVersion", status.StateVersion); writer.WriteString("underlyingStatus", EnumToken(status.UnderlyingStatus)); writer.WriteString("visibleStatus", EnumToken(status.VisibleStatus)); writer.WriteEndObject();
    }
    private static void WriteOptionalTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value) { if (value.HasValue) writer.WriteString(name, Timestamp(value.Value)); else writer.WriteNull(name); }
    private static DashboardProbeStatus NormalizeStatus(DashboardProbeStatus value) { if (!Enum.IsDefined(value)) Fail("Undefined DashboardProbeStatus."); return value; }
    private static string EnumToken<T>(T value) where T : struct, Enum { if (!Enum.IsDefined(value)) Fail("Undefined enum."); return value.ToString(); }
    private static string NormalizeUuid(string? value, string name) { if (value is null || !Guid.TryParseExact(value, "D", out var id)) throw new DashboardContractValidationException($"{name} is not UUID-D."); return id.ToString("D"); }
    private static string? NormalizeOptionalUuid(string? value, string name) => value is null ? null : NormalizeUuid(value, name);
    private static string? NormalizeText(string? value, string name) { if (value is null) return null; var normalized = value.Trim().Normalize(NormalizationForm.FormC); if (normalized.Length is 0 or > Wp07DashboardContract.MaximumFilterTextLength || HasControl(normalized)) Fail($"{name} is invalid."); return normalized; }
    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) { if (value.Offset != TimeSpan.Zero) Fail("Timestamp must be UTC."); return value; }
    private static string Timestamp(DateTimeOffset value) => NormalizePostgresTimestamp(value).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void ValidateStatusCounts(IReadOnlyList<DashboardStatusCount>? values) { var entries = values ?? throw new DashboardContractValidationException("StatusCounts is required."); if (entries.Count != StatusOrder.Length) Fail("StatusCounts must contain seven values."); for (var index = 0; index < StatusOrder.Length; index++) { var value = entries[index] ?? throw new DashboardContractValidationException("Null status count."); if (NormalizeStatus(value.Status) != StatusOrder[index] || value.Count < 0) Fail("StatusCounts order or count is invalid."); } }
    private static void ValidateRecentDown(IReadOnlyList<DashboardRecentDownItem>? values) { var entries = values ?? throw new DashboardContractValidationException("RecentlyDown is required."); ValidateCap(entries, "RecentlyDown"); var identities = new HashSet<string>(StringComparer.Ordinal); DashboardRecentDownItem? prior = null; foreach (var value in entries) { RequiredUuid(value.DeviceId, nameof(value.DeviceId)); RequiredUuid(value.ProbeId, nameof(value.ProbeId)); if (!identities.Add(value.ProbeId)) Fail("RecentlyDown contains duplicate ProbeId."); RequiredUuid(value.SiteId, nameof(value.SiteId)); RequiredUuid(value.OpenIncidentId, nameof(value.OpenIncidentId)); RequiredText(value.DeviceName, nameof(value.DeviceName)); RequiredText(value.SiteName, nameof(value.SiteName)); RequiredText(value.Criticality, nameof(value.Criticality)); OptionalText(value.Area, nameof(value.Area)); NormalizeUtc(value.SinceAt); if (prior is not null && CompareRecentDown(prior, value) > 0) Fail("RecentlyDown order is invalid."); prior = value; } }
    private static void ValidateOfflineAgents(IReadOnlyList<DashboardOfflineAgentItem>? values) { var entries = values ?? throw new DashboardContractValidationException("OfflineAgents is required."); ValidateCap(entries, "OfflineAgents"); var identities = new HashSet<string>(StringComparer.Ordinal); DashboardOfflineAgentItem? prior = null; foreach (var value in entries) { RequiredUuid(value.AgentId, nameof(value.AgentId)); if (!identities.Add(value.AgentId)) Fail("OfflineAgents contains duplicate AgentId."); RequiredUuid(value.AgentGroupId, nameof(value.AgentGroupId)); RequiredText(value.AgentName, nameof(value.AgentName)); RequiredText(value.Status, nameof(value.Status)); RequiredText(value.SelfHealth, nameof(value.SelfHealth)); if (!string.Equals(value.Status, "Offline", StringComparison.Ordinal)) Fail("OfflineAgents requires Offline."); if (value.LastHeartbeatAt.HasValue) NormalizeUtc(value.LastHeartbeatAt.Value); if (value.LastReportedAt.HasValue) NormalizeUtc(value.LastReportedAt.Value); if (prior is not null && CompareOffline(prior, value) > 0) Fail("OfflineAgents order is invalid."); prior = value; } }
    private static void ValidateOpenIncidents(IReadOnlyList<DashboardOpenIncidentItem>? values) { var entries = values ?? throw new DashboardContractValidationException("OpenIncidents is required."); ValidateCap(entries, "OpenIncidents"); var identities = new HashSet<string>(StringComparer.Ordinal); DashboardOpenIncidentItem? prior = null; foreach (var value in entries) { RequiredUuid(value.IncidentId, nameof(value.IncidentId)); if (!identities.Add(value.IncidentId)) Fail("OpenIncidents contains duplicate IncidentId."); RequiredUuid(value.DeviceId, nameof(value.DeviceId)); RequiredUuid(value.ProbeId, nameof(value.ProbeId)); RequiredUuid(value.SiteId, nameof(value.SiteId)); RequiredText(value.DeviceName, nameof(value.DeviceName)); RequiredText(value.SiteName, nameof(value.SiteName)); RequiredText(value.Criticality, nameof(value.Criticality)); if (value.OccurrenceCount < 0 || !Enum.IsDefined(value.Status) || value.Status is not (IncidentStatus.Open or IncidentStatus.Acknowledged)) Fail("Open incident is invalid."); NormalizeUtc(value.OpenedAt); if (prior is not null && CompareOpenIncident(prior, value) > 0) Fail("OpenIncidents order is invalid."); prior = value; } }
    private static void ValidateProbe(DeviceStatusResponse value) { RequiredUuid(value.ProbeId, nameof(value.ProbeId)); NormalizeStatus(value.VisibleStatus); NormalizeStatus(value.UnderlyingStatus); if (value.StateVersion < 0) Fail("StateVersion is negative."); if (value.LastFreshEventAt.HasValue) NormalizeUtc(value.LastFreshEventAt.Value); if (value.LastReceivedAt.HasValue) NormalizeUtc(value.LastReceivedAt.Value); if (value.LastFreshEventAt > value.LastReceivedAt) Fail("Fresh event follows receipt."); OptionalUuid(value.AgentId, nameof(value.AgentId)); OptionalUuid(value.OpenIncidentId, nameof(value.OpenIncidentId)); OptionalText(value.AgentName, nameof(value.AgentName)); if ((value.AgentId is null) != (value.AgentName is null)) Fail("Agent fields are ambiguous."); }
    private static int CompareRecentDown(DashboardRecentDownItem left, DashboardRecentDownItem right) { var compare = right.SinceAt.CompareTo(left.SinceAt); return compare != 0 ? compare : string.CompareOrdinal(right.ProbeId, left.ProbeId); }
    private static int CompareOffline(DashboardOfflineAgentItem left, DashboardOfflineAgentItem right) { var compare = Nullable.Compare(left.LastHeartbeatAt, right.LastHeartbeatAt); return compare != 0 ? compare : string.CompareOrdinal(left.AgentId, right.AgentId); }
    private static int CompareOpenIncident(DashboardOpenIncidentItem left, DashboardOpenIncidentItem right) { var compare = right.OpenedAt.CompareTo(left.OpenedAt); return compare != 0 ? compare : string.CompareOrdinal(right.IncidentId, left.IncidentId); }
    private static void ValidateCap<T>(IReadOnlyList<T>? values, string name) { if (values is null || values.Count > Wp07DashboardContract.DashboardSummaryListMaximum || values.Any(value => value is null)) Fail($"{name} is invalid."); }
    private static void RequiredUuid(string? value, string name) { if (value is null || !Guid.TryParseExact(value, "D", out var id)) throw new DashboardContractValidationException($"{name} is not canonical UUID-D."); if (!string.Equals(value, id.ToString("D"), StringComparison.Ordinal)) Fail($"{name} is not canonical UUID-D."); }
    private static void OptionalUuid(string? value, string name) { if (value is not null) RequiredUuid(value, name); }
    private static void RequiredText(string? value, string name) { if (value is null || value.Length is 0 or > 2048 || HasControl(value)) Fail($"{name} is required."); }
    private static void OptionalText(string? value, string name) { if (value is not null && (value.Length > 2048 || HasControl(value))) Fail($"{name} is invalid."); }
    private static bool HasControl(string value) => value.Any(character => char.IsControl(character));
    private static void Fail(string message) => throw new DashboardContractValidationException(message);
}

public enum DashboardIfNoneMatchClassification { Missing, NoMatch, Match, Invalid }
public sealed record DashboardNotModifiedMetadata(string Etag, string CacheControl, string CorrelationHeaderName, [property: CanonicalUuid] string CorrelationId);

public static class Wp07DashboardConditionalGet
{
    public static DashboardIfNoneMatchClassification ClassifyIfNoneMatch(IEnumerable<string>? values, string currentEtag)
    {
        if (!IsDashboardEtag(currentEtag)) throw new ArgumentException("Current ETag is invalid.", nameof(currentEtag));
        var headers = values?.ToArray() ?? [];
        if (headers.Length == 0) return DashboardIfNoneMatchClassification.Missing;
        if (!TryParse(headers, out var tags)) return DashboardIfNoneMatchClassification.Invalid;
        if (tags.Count == 1 && tags[0].Wildcard) return DashboardIfNoneMatchClassification.Match;
        if (tags.Any(tag => tag.Wildcard)) return DashboardIfNoneMatchClassification.Invalid;
        return tags.Any(tag => string.Equals(tag.Tag, currentEtag, StringComparison.Ordinal)) ? DashboardIfNoneMatchClassification.Match : DashboardIfNoneMatchClassification.NoMatch;
    }

    public static DashboardNotModifiedMetadata NotModifiedMetadata(string currentEtag, string correlationId) => new(currentEtag, Wp07DashboardContract.DashboardCacheControl, "X-Correlation-ID", correlationId);
    public static bool IsDashboardEtag(string? value) => value is not null && value.Length == Wp07DashboardCanonicalizer.EtagPrefix.Length + 43 + Wp07DashboardCanonicalizer.EtagSuffix.Length && value.StartsWith(Wp07DashboardCanonicalizer.EtagPrefix, StringComparison.Ordinal) && value.EndsWith(Wp07DashboardCanonicalizer.EtagSuffix, StringComparison.Ordinal) && value.AsSpan(Wp07DashboardCanonicalizer.EtagPrefix.Length, 43).ToString().All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool TryParse(IEnumerable<string> values, out List<(bool Wildcard, string Tag)> tags)
    {
        tags = [];
        foreach (var value in values)
        {
            if (value is null) return false;
            var index = 0; var expecting = true;
            while (true)
            {
                while (index < value.Length && value[index] is ' ' or '\t') index++;
                if (index == value.Length) { if (expecting) return false; break; }
                if (!expecting) return false;
                if (value[index] == '*') { tags.Add((true, string.Empty)); index++; }
                else
                {
                    if (value.AsSpan(index).StartsWith("W/", StringComparison.Ordinal)) index += 2;
                    if (index >= value.Length || value[index++] != '"') return false;
                    var start = index;
                    while (index < value.Length && value[index] != '"') { if (value[index] < '\x21' || value[index] > '\x7e') return false; index++; }
                    if (index == start || index == value.Length) return false;
                    tags.Add((false, "\"" + value[start..index++] + "\""));
                }
                while (index < value.Length && value[index] is ' ' or '\t') index++;
                if (index == value.Length) break;
                if (value[index++] != ',') return false;
                expecting = true;
            }
        }
        return tags.Count != 0;
    }
}

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
