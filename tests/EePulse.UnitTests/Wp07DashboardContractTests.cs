using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Dashboard;
using NodaTime.TimeZones;

namespace EePulse.UnitTests;

public sealed class Wp07DashboardContractTests
{
    private const string Id = "01234567-89ab-cdef-0123-456789abcdef";
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    [Fact]
    public void EveryDashboardTimestampHasUtcZConverterMetadata()
    {
        var timestamps = typeof(Wp07DashboardContract).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(Wp07DashboardContract).Namespace)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property.PropertyType == typeof(DateTimeOffset) || property.PropertyType == typeof(DateTimeOffset?))
            .ToArray();

        Assert.NotEmpty(timestamps);
        Assert.All(timestamps, property => Assert.Equal(typeof(UtcDateTimeOffsetJsonConverter), property.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType));
    }

    [Fact]
    public void UtcZConverterSerializesZRejectsNonZAndPreservesNull()
    {
        var response = new DashboardOfflineAgentItem(Id, "agent", Id, "offline", "healthy", Utc(1), null);
        var serialized = JsonSerializer.Serialize(response, Json);
        Assert.Contains("2026-09-07T01:00:00Z", serialized);
        Assert.Contains("\"lastReportedAt\":null", serialized);
        var roundTrip = JsonSerializer.Deserialize<DashboardOfflineAgentItem>(serialized, Json)!;
        Assert.Null(roundTrip.LastReportedAt);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MetricsQuery>("{\"from\":\"2026-09-07T01:02:03+07:00\",\"to\":\"2026-09-07T02:02:03Z\",\"resolution\":0,\"pointCount\":1}", Json));
    }

    [Fact]
    public void InboundContractsRejectUnmappedMembers()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AcknowledgeIncidentRequest>("{\"comment\":\"ok\",\"extra\":true}", Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DashboardInvalidationEvent>($"{{\"schemaVersion\":1,\"eventType\":\"status.changed\",\"entityId\":\"{Id}\",\"occurredAt\":\"2026-09-07T01:02:03Z\",\"detail\":{{}}}}", Json));
    }

    [Fact]
    public void CanonicalUuidMetadataRejectsNonCanonicalIdentifiers()
    {
        AssertValid(new DashboardInvalidationEvent(1, "status.changed", Id, null, null, null, 1, Utc(1)));
        foreach (var invalid in new[] { "", " ", Id + "\n", Id.ToUpperInvariant(), "{" + Id + "}", Id.Replace("-", ""), "01234567-89ab-cdef-0123-456789abcdeg" })
            AssertInvalid(new DashboardInvalidationEvent(1, "status.changed", invalid, null, null, null, 1, Utc(1)));
        AssertValid(new DashboardInvalidationEvent(1, "status.changed", Id, null, null, null, 1, Utc(1)));
        AssertValid(new IncidentListFilter(null, null, null, null, null, null, IncidentSort.OpenedAtDesc));
        AssertInvalid(new IncidentListFilter(Id.ToUpperInvariant(), null, null, null, null, null, IncidentSort.OpenedAtDesc));
        Assert.All(UuidProperties(), property => Assert.NotNull(property.GetCustomAttribute<CanonicalUuidAttribute>()));
    }

    [Fact]
    public void StrongEtagUsesDocumentedHttpVisibleAsciiSubset()
    {
        Assert.Equal("^\\\"[\\x21\\x23-\\x7E]+\\\"$", TimezonePreferenceContract.StrongEtagPattern);
        AssertValid(new TimezonePreferenceResponse(null, "\"tag!#[]\\\\~\""));
        foreach (var invalid in new[] { "tag", "W/\"tag\"", "\"\"", "\"has space\"", "\"has\\\"quote\"", "\"tab\t\"", "\"line\n\"", "\"del\u007f\"" })
            AssertInvalid(new TimezonePreferenceResponse(null, invalid));
    }

    [Fact]
    public void TimezoneBootstrapEtagsAndIfMatchClassificationAreFrozen()
    {
        Assert.Equal("\"tz-0\"", TimezonePreferenceContract.NoPersistedRowEtag);
        Assert.Equal("\"tz-0\"", TimezonePreferenceContract.EtagForVersion(0));
        Assert.Equal("\"tz-1\"", TimezonePreferenceContract.EtagForVersion(1));
        Assert.Equal("\"tz-2\"", TimezonePreferenceContract.EtagForVersion(2));
        Assert.Equal("\"tz-3\"", TimezonePreferenceContract.EtagForVersion(3));
        Assert.NotEqual(TimezonePreferenceContract.EtagForVersion(0), TimezonePreferenceContract.EtagForVersion(1));
        Assert.True(TimezonePreferenceContract.IsStrongEtag(TimezonePreferenceContract.EtagForVersion(0)));
        Assert.True(TimezonePreferenceContract.IsStrongEtag(TimezonePreferenceContract.EtagForVersion(42)));
        Assert.Equal(TimezoneIfMatchClassification.Missing, TimezonePreferenceContract.ClassifyIfMatch(null, "\"tz-0\""));
        Assert.Equal(TimezoneIfMatchClassification.Invalid, TimezonePreferenceContract.ClassifyIfMatch(["W/\"tz-0\""], "\"tz-0\""));
        Assert.Equal(TimezoneIfMatchClassification.Invalid, TimezonePreferenceContract.ClassifyIfMatch(["*"], "\"tz-0\""));
        Assert.Equal(TimezoneIfMatchClassification.Invalid, TimezonePreferenceContract.ClassifyIfMatch([""], "\"tz-0\""));
        Assert.Equal(TimezoneIfMatchClassification.Invalid, TimezonePreferenceContract.ClassifyIfMatch(["\"tz-0\"", "\"tz-1\""], "\"tz-0\""));
        Assert.Equal(TimezoneIfMatchClassification.Invalid, TimezonePreferenceContract.ClassifyIfMatch(["\"tz-0\", \"tz-1\""], "\"tz-0\""));
        Assert.Equal(TimezoneIfMatchClassification.Current, TimezonePreferenceContract.ClassifyIfMatch(["\"tz-0\""], "\"tz-0\""));
        Assert.Equal(TimezoneIfMatchClassification.Stale, TimezonePreferenceContract.ClassifyIfMatch(["\"tz-1\""], "\"tz-2\""));
        Assert.Equal(TimezoneIfMatchClassification.Stale, TimezonePreferenceContract.ClassifyIfMatch(["\"tz-0\""], "\"tz-1\""));

        // This is the concurrent first-write loser after the unique insert re-read: its tz-0 is stale.
        Assert.Equal(TimezoneIfMatchClassification.Stale, TimezonePreferenceContract.ClassifyIfMatch(
            [TimezonePreferenceContract.NoPersistedRowEtag], TimezonePreferenceContract.EtagForVersion(1)));
    }

    [Fact]
    public void TimezoneConditionalHeadersRejectNonCanonicalMutationTags()
    {
        Assert.False(TimezonePreferenceContract.TryGetIfMatchVersion(["\"tz-01\""], out _, out var classification));
        Assert.Equal(TimezoneIfMatchClassification.Invalid, classification);
        Assert.Equal(TimezoneIfNoneMatchClassification.Invalid,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["W/\"tz-01\""], "\"tz-1\"", true));
    }

    [Fact]
    public void TimezoneStateMachineFreezesNoOpClearAndVersionProgression()
    {
        var absent = TimezonePreferenceState.NoPersistedRow;
        Assert.False(absent.RowExists);
        Assert.Null(absent.Timezone);
        Assert.Equal("\"tz-0\"", absent.Etag);
        Assert.Same(absent, TimezonePreferenceContract.Advance(absent, null));

        var created = TimezonePreferenceContract.Advance(absent, "UTC");
        Assert.True(created.RowExists);
        Assert.Equal(TimezonePreferenceContract.CanonicalUtcId, created.Timezone);
        Assert.Equal(1, created.Version);
        Assert.Equal("\"tz-1\"", created.Etag);
        Assert.Same(created, TimezonePreferenceContract.Advance(created, "Etc/UTC"));

        var cleared = TimezonePreferenceContract.Advance(created, null);
        Assert.True(cleared.RowExists);
        Assert.Null(cleared.Timezone);
        Assert.Equal(2, cleared.Version);
        var reset = TimezonePreferenceContract.Advance(cleared, "Asia/Bangkok");
        Assert.Equal("Asia/Bangkok", reset.Timezone);
        Assert.Equal(3, reset.Version);
        Assert.Equal("\"tz-3\"", reset.Etag);
    }

    [Fact]
    public void TimezoneNormalizationAndMutationOutcomesAreHostIndependent()
    {
        Assert.Equal("Asia/Bangkok", Normalize("Asia/Bangkok"));
        Assert.Equal(TimezonePreferenceContract.CanonicalUtcId, Normalize("UTC"));
        Assert.Equal(TimezonePreferenceContract.CanonicalUtcId, Normalize("Etc/UTC"));
        Assert.Equal("Asia/Kolkata", Normalize("Asia/Calcutta"));
        AssertValid(new TimezonePreferenceRequest("UTC"));
        AssertValid(new TimezonePreferenceRequest("Etc/UTC"));
        foreach (var invalid in new[] { "Pacific Standard Time", "asia/bangkok", "Asia/Bangkok ", "", " ", "../Etc/UTC", "Asia\\Bangkok", "Not/AZone" })
        {
            Assert.False(TimezonePreferenceContract.TryNormalizeTimezone(invalid, out _));
            AssertInvalid(new TimezonePreferenceRequest(invalid));
        }
        Assert.True(TimezonePreferenceContract.TryNormalizeTimezone(null, out var cleared));
        Assert.Null(cleared);
        Assert.Equal(TimezonePreferenceMutationOutcome.NoOpAbsent, TimezonePreferenceContract.ClassifyMutation(false, null, null));
        Assert.Equal(TimezonePreferenceMutationOutcome.Create, TimezonePreferenceContract.ClassifyMutation(false, null, "UTC"));
        Assert.Equal(TimezonePreferenceMutationOutcome.NoOpCurrent, TimezonePreferenceContract.ClassifyMutation(true, "Etc/UTC", "UTC"));
        Assert.Equal(TimezonePreferenceMutationOutcome.Clear, TimezonePreferenceContract.ClassifyMutation(true, "Asia/Bangkok", null));
        Assert.Equal(TimezonePreferenceMutationOutcome.Update, TimezonePreferenceContract.ClassifyMutation(true, null, "Asia/Bangkok"));
    }

    [Fact]
    public void UtcNormalizationTracksThePinnedTzdbProviderCanonicalId()
    {
        var providerCanonicalUtc = TzdbDateTimeZoneSource.Default.CanonicalIdMap["UTC"];
        Assert.Equal("Etc/UTC", providerCanonicalUtc);
        Assert.True(TimezonePreferenceContract.TryNormalizeTimezone("UTC", out var normalized));
        Assert.Equal(providerCanonicalUtc, normalized);
    }

    [Fact]
    public void TimezonePrincipalComponentsFailClosedWithoutFallbacks()
    {
        Assert.True(TimezonePreferenceContract.IsValidPrincipalComponent("https://issuer.example", TimezonePreferenceContract.MaximumIssuerLength));
        Assert.True(TimezonePreferenceContract.IsValidPrincipalComponent(Id, TimezonePreferenceContract.MaximumSubjectLength));
        Assert.False(TimezonePreferenceContract.IsValidPrincipalComponent(null, TimezonePreferenceContract.MaximumIssuerLength));
        Assert.False(TimezonePreferenceContract.IsValidPrincipalComponent("", TimezonePreferenceContract.MaximumSubjectLength));
        Assert.False(TimezonePreferenceContract.IsValidPrincipalComponent(" ", TimezonePreferenceContract.MaximumSubjectLength));
        Assert.False(TimezonePreferenceContract.IsValidPrincipalComponent(new string('x', TimezonePreferenceContract.MaximumIssuerLength + 1), TimezonePreferenceContract.MaximumIssuerLength));
    }

    [Fact]
    public void BoundedRequestsFiltersAndTimezoneExposeRealValidationMetadata()
    {
        AssertInvalid(new AcknowledgeIncidentRequest(""));
        AssertInvalid(new ResolveIncidentRequest(new string('x', Wp07DashboardContract.MaximumNoteLength + 1)));
        AssertInvalid(new CursorPageRequest(new string('x', Wp07DashboardContract.MaximumCursorLength + 1), 0));
        AssertInvalid(new DashboardFilter(Id, new string('x', 129), null, null, null, null));
        AssertInvalid(new MetricsQuery(Utc(1), Utc(1), (ChartResolution)99, 0));
        AssertInvalid(new IncidentListFilter(Id, Id, Id, (IncidentStatus)99, Utc(2), Utc(1), (IncidentSort)99));
        AssertInvalid(new AuditLogFilter("a", "e", Id, Id, Utc(2), Utc(1), (AuditSort)99));
        AssertValid(new TimezonePreferenceRequest("Asia/Bangkok"));
        AssertInvalid(new TimezonePreferenceRequest("Not/AZone"));
        AssertValid(new TimezonePreferenceRequest(null));
        Assert.Equal(["persistedOverride", "selectedSite", "browser"], TimezonePreferenceContract.DisplayPrecedence);
    }

    [Fact]
    public void RolesManualResolutionAndInvalidationShapeAreFrozen()
    {
        Assert.Equal(["Operator", "Administrator"], DashboardAuthorization.IncidentOperateRoles);
        Assert.Equal(["Auditor", "Administrator"], DashboardAuthorization.AuditReadRoles);
        Assert.Equal(["Engineer", "Administrator"], DashboardAuthorization.InventoryMutationRoles);
        Assert.Equal(409, ManualResolutionConflictContract.StatusCode);
        Assert.Equal("incident-manual-resolution-state-conflict", Wp07DashboardContract.ManualResolutionStateConflict);
        Assert.Equal(["incidentId", "probeId", "underlyingStatus", "visibleStatus", "stateVersion", "currentEtag"], ManualResolutionConflictContract.RequiredExtensions);
        var eventData = new DashboardInvalidationEvent(1, "status.changed", Id, null, null, null, null, Utc(1));
        Assert.Equal($"{{\"schemaVersion\":1,\"eventType\":\"status.changed\",\"entityId\":\"{Id}\",\"deviceId\":null,\"probeId\":null,\"siteId\":null,\"version\":null,\"occurredAt\":\"2026-09-07T01:00:00Z\"}}", JsonSerializer.Serialize(eventData, Json));
        var names = typeof(DashboardInvalidationEvent).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("Detail", names);
        Assert.DoesNotContain("StatusCounts", names);
        Assert.DoesNotContain("Metrics", names);
        Assert.Equal("0", JsonSerializer.Serialize(DashboardProbeStatus.Unknown, Json));
        Assert.Equal("2", JsonSerializer.Serialize(IncidentStatus.Resolved, Json));
    }

    [Fact]
    public void Phase2B1SummaryHasNoGeneratedAtAndUsesIndependentCanonicalVector()
    {
        Assert.DoesNotContain(typeof(DashboardSummaryResponse).GetProperties(), property => property.Name == "GeneratedAt");
        var response = new DashboardSummaryResponse(1, new DashboardFilter(null, null, null, null, null, null), Counts(), [], [], []);
        const string expected = "{\"appliedFilter\":{\"area\":null,\"criticality\":null,\"deviceType\":null,\"siteId\":null,\"status\":null,\"tag\":null},\"offlineAgents\":[],\"openIncidents\":[],\"recentlyDown\":[],\"schemaVersion\":1,\"statusCounts\":[{\"count\":0,\"status\":\"Unknown\"},{\"count\":0,\"status\":\"Up\"},{\"count\":0,\"status\":\"Degraded\"},{\"count\":0,\"status\":\"Down\"},{\"count\":0,\"status\":\"Recovering\"},{\"count\":0,\"status\":\"Maintenance\"},{\"count\":0,\"status\":\"Disabled\"}]}";
        var bytes = Wp07DashboardCanonicalizer.SerializeSummary(response);
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(bytes));
        // Independently calculated SHA-256/base64url vector for the literal fixture above.
        Assert.Equal("\"wp07-2b1-sha256-ZbwxBPj5ue8V2Lhi9yqng6-89XjaLJEk3-39qc2mliI\"", Wp07DashboardCanonicalizer.EtagFor(bytes));
        Assert.Equal(43, Wp07DashboardCanonicalizer.EtagFor(bytes).Length - "\"wp07-2b1-sha256-".Length - 1);
    }

    [Fact]
    public void Phase2B1PopulatedSummaryUsesIndependentCanonicalVector()
    {
        const string expected = "{\"appliedFilter\":{\"area\":\"Caf\\u00E9\",\"criticality\":\"High\",\"deviceType\":\"Router\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"status\":\"Up\",\"tag\":\"a\\u0022b\"},\"offlineAgents\":[{\"agentGroupId\":\"22222222-2222-2222-2222-222222222222\",\"agentId\":\"33333333-3333-3333-3333-333333333333\",\"agentName\":\"Ag\\u00E9nt\",\"lastHeartbeatAt\":null,\"lastReportedAt\":null,\"selfHealth\":\"ok\",\"status\":\"Offline\"},{\"agentGroupId\":\"22222222-2222-2222-2222-222222222222\",\"agentId\":\"88888888-8888-8888-8888-888888888888\",\"agentName\":\"B\\u0022\",\"lastHeartbeatAt\":\"2026-09-07T01:00:00.0000000Z\",\"lastReportedAt\":\"2026-09-07T01:00:01.0000000Z\",\"selfHealth\":\"good\",\"status\":\"Offline\"}],\"openIncidents\":[{\"criticality\":\"High\",\"deviceId\":\"44444444-4444-4444-4444-444444444444\",\"deviceName\":\"D\\u00E9v\",\"incidentId\":\"77777777-7777-7777-7777-777777777777\",\"occurrenceCount\":2,\"openedAt\":\"2026-09-07T03:00:00.0000000Z\",\"probeId\":\"55555555-5555-5555-5555-555555555555\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteName\":\"S\\u0022\",\"status\":\"Open\"}],\"recentlyDown\":[{\"area\":null,\"criticality\":\"High\",\"deviceId\":\"44444444-4444-4444-4444-444444444444\",\"deviceName\":\"D\\u00E9v\",\"openIncidentId\":\"66666666-6666-6666-6666-666666666666\",\"probeId\":\"55555555-5555-5555-5555-555555555555\",\"sinceAt\":\"2026-09-07T04:00:00.1234560Z\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteName\":\"S\\u0022\"}],\"schemaVersion\":1,\"statusCounts\":[{\"count\":1,\"status\":\"Unknown\"},{\"count\":2,\"status\":\"Up\"},{\"count\":3,\"status\":\"Degraded\"},{\"count\":4,\"status\":\"Down\"},{\"count\":5,\"status\":\"Recovering\"},{\"count\":6,\"status\":\"Maintenance\"},{\"count\":7,\"status\":\"Disabled\"}]}";
        var filter = new DashboardFilter("11111111-1111-1111-1111-111111111111", "Caf\u00E9", "Router", "High", "a\"b", DashboardProbeStatus.Up);
        var summary = new DashboardSummaryResponse(1, filter, Enum.GetValues<DashboardProbeStatus>().Select((status, index) => new DashboardStatusCount(status, index + 1)).ToArray(),
            [new DashboardRecentDownItem("44444444-4444-4444-4444-444444444444", "D\u00E9v", "55555555-5555-5555-5555-555555555555", "11111111-1111-1111-1111-111111111111", "S\"", null, "High", new DateTimeOffset(2026, 9, 7, 4, 0, 0, TimeSpan.Zero).AddTicks(1234560), "66666666-6666-6666-6666-666666666666")],
            [new DashboardOfflineAgentItem("33333333-3333-3333-3333-333333333333", "Ag\u00E9nt", "22222222-2222-2222-2222-222222222222", "Offline", "ok", null, null), new DashboardOfflineAgentItem("88888888-8888-8888-8888-888888888888", "B\"", "22222222-2222-2222-2222-222222222222", "Offline", "good", Utc(1), Utc(1).AddSeconds(1))],
            [new DashboardOpenIncidentItem("77777777-7777-7777-7777-777777777777", "44444444-4444-4444-4444-444444444444", "D\u00E9v", "55555555-5555-5555-5555-555555555555", "11111111-1111-1111-1111-111111111111", "S\"", IncidentStatus.Open, Utc(3), 2, "High")]);
        var bytes = Wp07DashboardCanonicalizer.SerializeSummary(summary);
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal("\"wp07-2b1-sha256-yw0YRZ1yRGiGpzLr-XanqM3Byluo04PQxjXTn9maCew\"", Wp07DashboardCanonicalizer.EtagFor(bytes));
        Assert.Equal(bytes, Wp07DashboardCanonicalizer.SerializeSummary(summary with { AppliedFilter = filter with { SiteId = filter.SiteId!.ToUpperInvariant(), Area = " Caf\u00E9 ", Tag = "a\"b" } }));
    }

    [Fact]
    public void Phase2B1CanonicalWriterOwnsOrderingNullsUtcAndEscaping()
    {
        const string upper = "01234567-89ab-cdef-0123-456789abcdef";
        var status = new DeviceStatusResponse(upper, DashboardProbeStatus.Down, DashboardProbeStatus.Down, 7,
            new DateTimeOffset(2026, 9, 7, 1, 2, 3, TimeSpan.Zero).AddTicks(4567899), null, null, null, null);
        var device = new StatusEnrichedDeviceResponse(upper, upper, "café", "127.0.0.1", null, "type", null, null, "high", ["x"], true, 1, [status]);
        var text = System.Text.Encoding.UTF8.GetString(Wp07DashboardCanonicalizer.SerializeDeviceStatus(device));
        Assert.StartsWith("{\"address\":\"127.0.0.1\",\"area\":null,\"criticality\":\"high\"", text);
        Assert.Contains("\\u00E9", text, StringComparison.Ordinal);
        Assert.Contains("\"agentId\":null", text, StringComparison.Ordinal);
        Assert.Contains("\"lastReceivedAt\":null", text, StringComparison.Ordinal);
        Assert.Contains("2026-09-07T01:02:03.4567890Z", text, StringComparison.Ordinal);
        Assert.Contains(Id, text, StringComparison.Ordinal);
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeDeviceStatus(device with { Probes = [status with { LastFreshEventAt = status.LastFreshEventAt!.Value.ToOffset(TimeSpan.FromHours(7)) }] }));
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero).AddTicks(1234560), Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero).AddTicks(1234567)));
    }

    [Fact]
    public void Phase2B1PopulatedDeviceUsesIndependentCanonicalVector()
    {
        const string expected = "{\"address\":\"addr\\u0022\",\"area\":null,\"criticality\":\"Caf\\u00E9\",\"deviceType\":\"Type\",\"enabled\":true,\"hostname\":null,\"id\":\"99999999-9999-9999-9999-999999999999\",\"name\":\"N\\u00E9\",\"owner\":\"O\\u0022\",\"probes\":[{\"agentId\":null,\"agentName\":null,\"lastFreshEventAt\":null,\"lastReceivedAt\":null,\"openIncidentId\":null,\"probeId\":\"10101010-1010-1010-1010-101010101010\",\"stateVersion\":0,\"underlyingStatus\":\"Unknown\",\"visibleStatus\":\"Unknown\"},{\"agentId\":\"30303030-3030-3030-3030-303030303030\",\"agentName\":\"A\\u00E9\",\"lastFreshEventAt\":\"2026-09-07T01:00:00.1234560Z\",\"lastReceivedAt\":\"2026-09-07T01:00:01.0000000Z\",\"openIncidentId\":\"40404040-4040-4040-4040-404040404040\",\"probeId\":\"20202020-2020-2020-2020-202020202020\",\"stateVersion\":7,\"underlyingStatus\":\"Down\",\"visibleStatus\":\"Recovering\"}],\"rowVersion\":9,\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"tags\":[\"A\",\"a\",\"z\"]}";
        var first = new DeviceStatusResponse("10101010-1010-1010-1010-101010101010", DashboardProbeStatus.Unknown, DashboardProbeStatus.Unknown, 0, null, null, null, null, null);
        var second = new DeviceStatusResponse("20202020-2020-2020-2020-202020202020", DashboardProbeStatus.Recovering, DashboardProbeStatus.Down, 7, Utc(1).AddTicks(1234560), Utc(1).AddSeconds(1), "30303030-3030-3030-3030-303030303030", "A\u00E9", "40404040-4040-4040-4040-404040404040");
        var device = new StatusEnrichedDeviceResponse("99999999-9999-9999-9999-999999999999", "11111111-1111-1111-1111-111111111111", "N\u00E9", "addr\"", null, "Type", null, "O\"", "Caf\u00E9", ["z", "a", "A"], true, 9, [second, first]);
        var bytes = Wp07DashboardCanonicalizer.SerializeDeviceStatus(device);
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal("\"wp07-2b1-sha256-M0bNMN_R3pQqjWaB8KZGmhNzWP3FtzEfn1MA9xtWSv4\"", Wp07DashboardCanonicalizer.EtagFor(bytes));
        Assert.Equal(bytes, Wp07DashboardCanonicalizer.SerializeDeviceStatus(device with { Tags = ["A", "a", "z"], Probes = [first, second] }));
        Assert.Equal(["z", "a", "A"], device.Tags); Assert.Equal([second.ProbeId, first.ProbeId], device.Probes.Select(probe => probe.ProbeId));
    }

    [Fact]
    public void Phase2B1StatusOverlayAndCappedOrdersAreFrozen()
    {
        Assert.Equal(DashboardProbeStatus.Disabled, Wp07DashboardCanonicalizer.ResolveVisibleStatus(false, true, true, DashboardProbeStatus.Up));
        Assert.Equal(DashboardProbeStatus.Disabled, Wp07DashboardCanonicalizer.ResolveVisibleStatus(true, false, true, DashboardProbeStatus.Up));
        Assert.Equal(DashboardProbeStatus.Maintenance, Wp07DashboardCanonicalizer.ResolveVisibleStatus(true, true, true, DashboardProbeStatus.Down));
        Assert.Equal(DashboardProbeStatus.Down, Wp07DashboardCanonicalizer.ResolveVisibleStatus(true, true, false, DashboardProbeStatus.Down));
        var missing = Wp07DashboardCanonicalizer.MissingProjection(Id);
        Assert.Equal((DashboardProbeStatus.Unknown, DashboardProbeStatus.Unknown, 0L), (missing.VisibleStatus, missing.UnderlyingStatus, missing.StateVersion));
        Assert.Null(missing.LastFreshEventAt); Assert.Null(missing.LastReceivedAt); Assert.Null(missing.AgentId); Assert.Null(missing.OpenIncidentId);

        var later = new DashboardRecentDownItem(Id, "d", "00000000-0000-0000-0000-000000000002", Id, "s", null, "c", Utc(2), Id);
        var earlier = later with { ProbeId = "00000000-0000-0000-0000-000000000001", SinceAt = Utc(1) };
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeSummary(new DashboardSummaryResponse(1, new DashboardFilter(null, null, null, null, null, null), Counts(), [earlier, later], [], [])));
    }

    [Fact]
    public void Phase2B1FiltersEtagsAndConditionalGetAreRepresentationOwned()
    {
        var normalized = Wp07DashboardCanonicalizer.NormalizeFilter(new DashboardFilter(Id.ToUpperInvariant(), " café ", " type ", " c ", " tag ", DashboardProbeStatus.Up));
        var equivalent = Wp07DashboardCanonicalizer.NormalizeFilter(new DashboardFilter(Id, "café", "type", "c", "tag", DashboardProbeStatus.Up));
        Assert.Equal(normalized, equivalent);
        var first = new DashboardSummaryResponse(1, normalized, Counts(), [], [], []);
        var second = new DashboardSummaryResponse(1, equivalent, Counts(), [], [], []);
        Assert.Equal(Wp07DashboardCanonicalizer.SerializeSummary(first), Wp07DashboardCanonicalizer.SerializeSummary(second));
        Assert.Equal(Wp07DashboardCanonicalizer.EtagForSummary(first), Wp07DashboardCanonicalizer.EtagForSummary(second));
        Assert.NotEqual(Wp07DashboardCanonicalizer.EtagFor([1]), Wp07DashboardCanonicalizer.EtagFor([2]));
        var tag = Wp07DashboardCanonicalizer.EtagForSummary(first);
        Assert.Equal(DashboardIfNoneMatchClassification.Match, Wp07DashboardConditionalGet.ClassifyIfNoneMatch(["W/" + tag, "\"other\", " + tag], tag));
        Assert.Equal(DashboardIfNoneMatchClassification.Match, Wp07DashboardConditionalGet.ClassifyIfNoneMatch(["*"], tag));
        Assert.Equal(DashboardIfNoneMatchClassification.NoMatch, Wp07DashboardConditionalGet.ClassifyIfNoneMatch(["\"a,b\""], tag));
        Assert.Equal(DashboardIfNoneMatchClassification.Invalid, Wp07DashboardConditionalGet.ClassifyIfNoneMatch(["*, " + tag], tag));
        Assert.Equal(DashboardIfNoneMatchClassification.Invalid, Wp07DashboardConditionalGet.ClassifyIfNoneMatch(["W/"], tag));
        var metadata = Wp07DashboardConditionalGet.NotModifiedMetadata(tag, Id);
        Assert.Equal((tag, "private, max-age=0, must-revalidate", "X-Correlation-ID", Id), (metadata.Etag, metadata.CacheControl, metadata.CorrelationHeaderName, metadata.CorrelationId));
    }

    [Fact]
    public void Phase2B1SummarySerializesOneNormalizedAppliedFilterAndRejectsDuplicateSemanticIdentities()
    {
        var unnormalized = new DashboardSummaryResponse(1, new DashboardFilter(Id.ToUpperInvariant(), " area ", "type", "critical", "e\u0301", DashboardProbeStatus.Up), Counts(), [], [], []);
        var normalized = new DashboardSummaryResponse(1, new DashboardFilter(Id, "area", "type", "critical", "é", DashboardProbeStatus.Up), Counts(), [], [], []);
        Assert.Equal(Wp07DashboardCanonicalizer.SerializeSummary(normalized), Wp07DashboardCanonicalizer.SerializeSummary(unnormalized));
        Assert.Equal(Wp07DashboardCanonicalizer.EtagForSummary(normalized), Wp07DashboardCanonicalizer.EtagForSummary(unnormalized));
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeSummary(unnormalized with { AppliedFilter = new DashboardFilter(null, " ", null, null, null, null) }));
        var recent = new DashboardRecentDownItem(Id, "d", "00000000-0000-0000-0000-000000000001", Id, "s", null, "c", Utc(2), Id);
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeSummary(normalized with { RecentlyDown = [recent, recent with { DeviceName = "other" }] }));
        var agent = new DashboardOfflineAgentItem("00000000-0000-0000-0000-000000000001", "a", Id, "Offline", "ok", null, null);
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeSummary(normalized with { OfflineAgents = [agent, agent with { AgentName = "other" }] }));
        var incident = new DashboardOpenIncidentItem("00000000-0000-0000-0000-000000000001", Id, "d", Id, Id, "s", IncidentStatus.Open, Utc(2), 1, "c");
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeSummary(normalized with { OpenIncidents = [incident, incident with { DeviceName = "other" }] }));
    }

    [Fact]
    public void Phase2B1ResponseValidationAndCanonicalCollectionsFailClosed()
    {
        var firstProbe = new DeviceStatusResponse("00000000-0000-0000-0000-000000000001", DashboardProbeStatus.Up, DashboardProbeStatus.Up, 0, null, null, null, null, null);
        var secondProbe = firstProbe with { ProbeId = "00000000-0000-0000-0000-000000000002" };
        var device = new StatusEnrichedDeviceResponse(Id, Id, "name", "127.0.0.1", null, "type", null, null, "critical", ["z", "A"], true, 0, [secondProbe, firstProbe]);
        var canonical = Wp07DashboardCanonicalizer.SerializeDeviceStatus(device);
        Assert.Equal(canonical, Wp07DashboardCanonicalizer.SerializeDeviceStatus(device with { Tags = ["A", "z"], Probes = [firstProbe, secondProbe] }));
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeDeviceStatus(device with { Tags = ["z", "z"] }));
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeDeviceStatus(device with { Probes = [firstProbe, firstProbe] }));
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeDeviceStatus(device with { RowVersion = -1 }));
        Assert.Throws<DashboardContractValidationException>(() => Wp07DashboardCanonicalizer.SerializeDeviceStatus(device with { Probes = [firstProbe with { VisibleStatus = (DashboardProbeStatus)99 }] }));
        Assert.All(Wp07DashboardCanonicalizer.CanonicalWireProperties, pair => Assert.Equal(pair.Key.GetProperties().Select(property => property.Name).Order(), pair.Value.Order()));
    }

    private static IEnumerable<PropertyInfo> UuidProperties() => typeof(Wp07DashboardContract).Assembly.GetTypes()
        .Where(type => type.Namespace == typeof(Wp07DashboardContract).Namespace)
        .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        .Where(property => property.Name is "Id" or "SiteId" or "DeviceId" or "ProbeId" or "AgentId" or "AgentGroupId" or "OpenIncidentId" or "IncidentId" or "TransitionId" or "EventId" or "ActorId" or "AuthorId" or "EntityId" or "CorrelationId");

    private static DateTimeOffset Utc(int hour) => new(2026, 9, 7, hour, 0, 0, TimeSpan.Zero);
    private static DashboardStatusCount[] Counts() => Enum.GetValues<DashboardProbeStatus>().Select(status => new DashboardStatusCount(status, 0)).ToArray();
    private static string Normalize(string value)
    {
        Assert.True(TimezonePreferenceContract.TryNormalizeTimezone(value, out var normalized));
        return Assert.IsType<string>(normalized);
    }
    private static JsonSerializerOptions CreateJsonOptions() { var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); AgentJsonContract.AddConverters(options); return options; }
    private static void AssertValid(object instance) => Assert.Empty(Validate(instance));
    private static void AssertInvalid(object instance) => Assert.NotEmpty(Validate(instance));
    private static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, true);
        if (instance is IValidatableObject validatable) results.AddRange(validatable.Validate(new ValidationContext(instance)));
        return results;
    }
}
