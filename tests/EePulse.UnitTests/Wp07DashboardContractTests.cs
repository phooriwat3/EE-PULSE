using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Dashboard;

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

    private static IEnumerable<PropertyInfo> UuidProperties() => typeof(Wp07DashboardContract).Assembly.GetTypes()
        .Where(type => type.Namespace == typeof(Wp07DashboardContract).Namespace)
        .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        .Where(property => property.Name is "Id" or "SiteId" or "DeviceId" or "ProbeId" or "AgentId" or "AgentGroupId" or "OpenIncidentId" or "IncidentId" or "TransitionId" or "EventId" or "ActorId" or "AuthorId" or "EntityId" or "CorrelationId");

    private static DateTimeOffset Utc(int hour) => new(2026, 9, 7, hour, 0, 0, TimeSpan.Zero);
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
