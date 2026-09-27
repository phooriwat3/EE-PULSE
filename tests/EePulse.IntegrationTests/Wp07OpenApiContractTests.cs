using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EePulse.IntegrationTests;

public sealed class Wp07OpenApiContractTests
{
    private static readonly string[] OpenDashboardIncidentStatuses = ["Open", "Acknowledged"];
    [Fact]
    public async Task CheckedInArtifactIsSemanticallyEqualToRuntimeGeneratedDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = await File.ReadAllBytesAsync(CheckedInArtifactPath(), ct);
        await using var factory = new WebApplicationFactory<Program>().WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing());
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", ct);
        response.EnsureSuccessStatusCode();

        var actual = await response.Content.ReadAsByteArrayAsync(ct);
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.Equal("http://localhost:8080/", expectedDocument.RootElement.GetProperty("servers")[0].GetProperty("url").GetString());
        Assert.Equal("http://localhost/", actualDocument.RootElement.GetProperty("servers")[0].GetProperty("url").GetString());
        var difference = FindDifference(expectedDocument.RootElement, actualDocument.RootElement, "$");
        Assert.True(difference is null, $"The checked-in OpenAPI artifact differs semantically from the runtime-generated document at {difference}.");
    }

    [Fact]
    public async Task GeneratedDocumentPublishesOnlyTheFrozenWp07UiEnablementRoutesAndSchemas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WebApplicationFactory<Program>().WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing());
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var paths = document.RootElement.GetProperty("paths");

        AssertOperation(paths, "/api/v1/dashboard/summary", "get", "GetDashboardSummary", "DashboardSummaryResponse", ["200", "304", "400", "401", "403", "503"],
            ["siteId", "area", "deviceType", "criticality", "tag", "status", "If-None-Match", "If-Match"]);
        AssertOperation(paths, "/api/v1/devices/{id}/status", "get", "GetDeviceStatus", "StatusEnrichedDeviceResponse", ["200", "304", "400", "401", "403", "404", "503"],
            ["id", "If-None-Match", "If-Match"]);
        AssertOperation(paths, "/api/v1/incidents", "get", "ListIncidents", "CursorPageOfIncidentResponse", ["200", "400", "401", "403", "503"],
            ["siteId", "deviceId", "probeId", "status", "openedFrom", "openedTo", "sort", "cursor", "pageSize", "If-Match"]);
        AssertOperation(paths, "/api/v1/incidents/{id}", "get", "GetIncident", "IncidentResponse", ["200", "304", "400", "401", "403", "404", "503"],
            ["id", "If-Match", "If-None-Match"]);
        AssertOperation(paths, "/api/v1/devices/{id}/incidents", "get", "ListDeviceIncidents", "CursorPageOfIncidentResponse", ["200", "400", "401", "403", "404", "503"],
            ["id", "status", "openedFrom", "openedTo", "sort", "cursor", "pageSize", "If-Match"]);
        AssertOperation(paths, "/api/v1/incidents/{id}/lifecycle-events", "get", "ListIncidentLifecycleEvents", "CursorPageOfIncidentLifecycleResponse", ["200", "400", "401", "403", "404", "503"],
            ["id", "sort", "cursor", "pageSize"]);
        AssertOperation(paths, "/api/v1/incidents/{id}/comments", "get", "ListIncidentComments", "CursorPageOfIncidentCommentResponse", ["200", "400", "401", "403", "404", "503"],
            ["id", "sort", "cursor", "pageSize"]);
        AssertOperation(paths, "/api/v1/incidents/{id}/acknowledge", "post", "AcknowledgeIncident", "IncidentActionResponse", ["200", "400", "401", "403", "404", "409", "412", "428", "503"], ["id", "Idempotency-Key", "If-Match"], "AcknowledgeIncidentRequest");
        AssertOperation(paths, "/api/v1/incidents/{id}/comments", "post", "AddIncidentComment", "IncidentCommentResponse", ["201", "400", "401", "403", "404", "409", "412", "428", "503"], ["id", "Idempotency-Key", "If-Match"], "AddIncidentCommentRequest");
        AssertOperation(paths, "/api/v1/incidents/{id}/resolve", "post", "ResolveIncident", "IncidentActionResponse", ["200", "400", "401", "403", "404", "409", "412", "428", "503"], ["id", "Idempotency-Key", "If-Match"], "ResolveIncidentRequest");

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schema in new[] { "DashboardSummaryResponse", "StatusEnrichedDeviceResponse", "IncidentResponse", "IncidentLifecycleResponse", "IncidentCommentResponse", "IncidentActionResponse", "AcknowledgeIncidentRequest", "AddIncidentCommentRequest", "ResolveIncidentRequest", "CursorPageOfIncidentResponse", "CursorPageOfIncidentLifecycleResponse", "CursorPageOfIncidentCommentResponse" })
            Assert.True(schemas.TryGetProperty(schema, out _), $"Missing generated schema {schema}.");
        foreach (var forbidden in new[] { "IncidentCursorKey", "IdempotencyReceipt", "PrincipalIdentity", "Transaction", "StoredIncidentHttpResponse" })
            Assert.False(schemas.TryGetProperty(forbidden, out _), $"Internal schema leaked: {forbidden}.");
    }

    [Fact]
    public async Task GeneratedDocumentPublishesCanonicalWp07WireSchemasAndParameterBounds()
    {
        using var document = await GetDocumentAsync();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        AssertStringEnum(schemas.GetProperty("DashboardProbeStatus"), ["Unknown", "Up", "Degraded", "Down", "Recovering", "Maintenance", "Disabled"]);
        AssertStringEnum(schemas.GetProperty("IncidentStatus"), ["Open", "Acknowledged", "Resolved"]);

        AssertTimestamp(schemas, "DashboardRecentDownItem", "sinceAt", false);
        AssertTimestamp(schemas, "DashboardOfflineAgentItem", "lastHeartbeatAt", true);
        AssertTimestamp(schemas, "DashboardOfflineAgentItem", "lastReportedAt", true);
        AssertTimestamp(schemas, "DashboardOpenIncidentItem", "openedAt", false);
        AssertTimestamp(schemas, "DeviceStatusResponse", "lastFreshEventAt", true);
        AssertTimestamp(schemas, "DeviceStatusResponse", "lastReceivedAt", true);
        AssertTimestamp(schemas, "IncidentResponse", "openedAt", false);
        AssertTimestamp(schemas, "IncidentResponse", "acknowledgedAt", true);
        AssertTimestamp(schemas, "IncidentResponse", "resolvedAt", true);
        AssertTimestamp(schemas, "IncidentLifecycleResponse", "occurredAt", false);
        AssertTimestamp(schemas, "IncidentCommentResponse", "createdAt", false);
        AssertTimestamp(schemas, "IncidentActionResponse", "completedAt", false);

        AssertCanonicalUuid(schemas, "IncidentResponse", "id");
        AssertCanonicalUuid(schemas, "DashboardRecentDownItem", "openIncidentId");
        var recentDown = schemas.GetProperty("DashboardRecentDownItem");
        Assert.Equal("string", recentDown.GetProperty("properties").GetProperty("openIncidentId").GetProperty("type").GetString());
        Assert.Contains(recentDown.GetProperty("required").EnumerateArray(), value => value.GetString() == "openIncidentId");
        AssertCanonicalUuid(schemas, "DashboardFilter", "siteId");
        AssertCanonicalUuid(schemas, "IncidentLifecycleResponse", "eventId");
        AssertCanonicalUuid(schemas, "IncidentCommentResponse", "authorId");
        Assert.Equal(1, schemas.GetProperty("IncidentResponse").GetProperty("properties").GetProperty("occurrenceCount").GetProperty("minimum").GetInt32());
        Assert.Equal(OpenDashboardIncidentStatuses, schemas.GetProperty("DashboardOpenIncidentItem").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Contains("fixed order: Unknown, Up, Degraded, Down, Recovering, Maintenance, Disabled", schemas.GetProperty("DashboardSummaryResponse").GetProperty("properties").GetProperty("statusCounts").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(5, schemas.GetProperty("IncidentLifecycleResponse").GetProperty("oneOf").GetArrayLength());
        foreach (var (schemaName, propertyName) in new[]
        {
            ("IncidentResponse", "acknowledgementComment"), ("IncidentResponse", "resolutionNote"),
            ("IncidentLifecycleResponse", "comment"), ("IncidentCommentResponse", "comment")
        })
        {
            var textSchema = schemas.GetProperty(schemaName).GetProperty("properties").GetProperty(propertyName);
            Assert.False(textSchema.TryGetProperty("maxLength", out _));
            Assert.Contains("UTF-16 code units", textSchema.GetProperty("description").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(20, schemas.GetProperty("DashboardSummaryResponse").GetProperty("properties").GetProperty("openIncidents").GetProperty("maxItems").GetInt32());
        AssertStringEnum(schemas.GetProperty("IncidentLifecycleResponse").GetProperty("properties").GetProperty("type"), ["opened", "resolved", "occurrence", "acknowledged", "manually-resolved"]);
        AssertStringEnum(schemas.GetProperty("IncidentLifecycleResponse").GetProperty("properties").GetProperty("reasonCode"), ["failure-threshold-met", "recovery-threshold-met", "recovery-failed", "operator-acknowledgement", "manual-resolution"]);

        var summary = document.RootElement.GetProperty("paths").GetProperty("/api/v1/dashboard/summary").GetProperty("get");
        var siteId = Parameter(summary, "siteId").GetProperty("schema");
        Assert.Equal("^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", siteId.GetProperty("pattern").GetString());
        foreach (var name in new[] { "area", "deviceType", "criticality", "tag" })
        {
            Assert.Equal(1, Parameter(summary, name).GetProperty("schema").GetProperty("minLength").GetInt32());
            Assert.False(Parameter(summary, name).TryGetProperty("required", out var required) && required.GetBoolean());
            Assert.False(Parameter(summary, name).TryGetProperty("allowEmptyValue", out var allowEmpty) && allowEmpty.GetBoolean());
            var filterSchema = schemas.GetProperty("DashboardFilter").GetProperty("properties").GetProperty(name);
            Assert.Equal(1, filterSchema.GetProperty("minLength").GetInt32());
            Assert.False(filterSchema.TryGetProperty("maxLength", out _));
            Assert.False(Parameter(summary, name).GetProperty("schema").TryGetProperty("maxLength", out _));
            Assert.Contains("NFC-normalized", Parameter(summary, name).GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Contains("128 UTF-16 code units", Parameter(summary, name).GetProperty("description").GetString(), StringComparison.Ordinal);
        }

        var incidents = document.RootElement.GetProperty("paths").GetProperty("/api/v1/incidents").GetProperty("get");
        AssertStringEnum(Parameter(incidents, "status").GetProperty("schema"), ["Open", "Acknowledged", "Resolved"]);
        AssertStringEnum(Parameter(incidents, "sort").GetProperty("schema"), ["OpenedAtDesc", "OpenedAtAsc"]);
        Assert.Equal(2048, Parameter(incidents, "cursor").GetProperty("schema").GetProperty("maxLength").GetInt32());
        foreach (var path in new[] { "/api/v1/incidents", "/api/v1/devices/{id}/incidents", "/api/v1/incidents/{id}/lifecycle-events", "/api/v1/incidents/{id}/comments" })
        {
            var cursor = Parameter(document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get"), "cursor");
            Assert.Equal(1, cursor.GetProperty("schema").GetProperty("minLength").GetInt32());
            Assert.False(cursor.TryGetProperty("required", out var required) && required.GetBoolean());
            Assert.False(cursor.TryGetProperty("allowEmptyValue", out var allowEmpty) && allowEmpty.GetBoolean());
            Assert.Contains("Omission starts the query", cursor.GetProperty("description").GetString(), StringComparison.Ordinal);
        }
        foreach (var schemaName in new[] { "CursorPageOfIncidentResponse", "CursorPageOfIncidentLifecycleResponse", "CursorPageOfIncidentCommentResponse" })
            Assert.Equal(1, schemas.GetProperty(schemaName).GetProperty("properties").GetProperty("nextCursor").GetProperty("minLength").GetInt32());
        var pageSize = Parameter(incidents, "pageSize").GetProperty("schema");
        Assert.Equal(1, pageSize.GetProperty("minimum").GetInt32());
        Assert.Equal(200, pageSize.GetProperty("maximum").GetInt32());
        Assert.Equal(50, pageSize.GetProperty("default").GetInt32());
        Assert.Equal("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?Z$", Parameter(incidents, "openedFrom").GetProperty("schema").GetProperty("pattern").GetString());
    }

    [Fact]
    public async Task GeneratedDocumentDescribesCommandConditionalAndReplaySemanticsPerRoute()
    {
        using var document = await GetDocumentAsync();
        var paths = document.RootElement.GetProperty("paths");
        var detail = paths.GetProperty("/api/v1/incidents/{id}").GetProperty("get");
        var conditional = Parameter(detail, "If-None-Match").GetProperty("description").GetString();
        Assert.Contains("every empty list member", conditional, StringComparison.Ordinal);
        Assert.Contains("weak comparison", conditional, StringComparison.Ordinal);
        Assert.Contains("wildcard", conditional, StringComparison.Ordinal);

        foreach (var (path, status) in new[]
        {
            ("/api/v1/incidents/{id}/acknowledge", "200"), ("/api/v1/incidents/{id}/comments", "201"), ("/api/v1/incidents/{id}/resolve", "200")
        })
        {
            var command = paths.GetProperty(path).GetProperty("post");
            Assert.Contains("case-insensitively", command.GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Contains("quoted empty strong opaque tag", Parameter(command, "If-Match").GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Contains("all-zero", Parameter(command, "Idempotency-Key").GetProperty("description").GetString(), StringComparison.Ordinal);
            var idempotencyPattern = Parameter(command, "Idempotency-Key").GetProperty("schema").GetProperty("pattern").GetString();
            Assert.StartsWith("^(?!00000000-0000-0000-0000-000000000000$)", idempotencyPattern, StringComparison.Ordinal);
            var successEtag = command.GetProperty("responses").GetProperty(status).GetProperty("headers").GetProperty("ETag").GetProperty("description").GetString();
            Assert.Contains("Fresh result", successEtag, StringComparison.Ordinal);
            Assert.Contains("Exact idempotency replay", successEtag, StringComparison.Ordinal);
        }

        foreach (var path in new[] { "/api/v1/incidents/{id}/acknowledge", "/api/v1/incidents/{id}/resolve" })
        {
            var responses = paths.GetProperty(path).GetProperty("post").GetProperty("responses");
            var conflict = responses.GetProperty("409");
            Assert.True(conflict.GetProperty("headers").TryGetProperty("ETag", out _));
            Assert.Contains("absent for idempotency-key-reuse-conflict", conflict.GetProperty("headers").GetProperty("ETag").GetProperty("description").GetString(), StringComparison.Ordinal);
            var expectedStatus = path.EndsWith("acknowledge", StringComparison.Ordinal) ? "Acknowledged" : "Resolved";
            Assert.Equal(expectedStatus, responses.GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("allOf")[1].GetProperty("properties").GetProperty("status").GetProperty("enum")[0].GetString());
        }

        var comment = paths.GetProperty("/api/v1/incidents/{id}/comments").GetProperty("post");
        Assert.False(comment.GetProperty("responses").GetProperty("409").TryGetProperty("headers", out var commentHeaders) && commentHeaders.TryGetProperty("ETag", out _));
        foreach (var path in new[] { "/api/v1/incidents/{id}/acknowledge", "/api/v1/incidents/{id}/resolve", "/api/v1/incidents/{id}/comments" })
        {
            var command = paths.GetProperty(path).GetProperty("post");
            var forbidden = command.GetProperty("responses").GetProperty("403").GetProperty("description").GetString();
            Assert.Contains("without a stable code or correlationId extension", forbidden, StringComparison.Ordinal);
            Assert.Contains("invalid-incident-actor-identity", forbidden, StringComparison.Ordinal);
            var textSchema = command.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            var requestSchema = textSchema.TryGetProperty("$ref", out var requestRef)
                ? document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(requestRef.GetString()!.Split('/').Last())
                : textSchema;
            var propertyName = path.EndsWith("resolve", StringComparison.Ordinal) ? "note" : "comment";
            var textProperty = requestSchema.GetProperty("properties").GetProperty(propertyName);
            Assert.False(textProperty.TryGetProperty("maxLength", out _));
            Assert.Contains("UTF-16 code units", textProperty.GetProperty("description").GetString(), StringComparison.Ordinal);
        }
        var resolveConflict = paths.GetProperty("/api/v1/incidents/{id}/resolve").GetProperty("post").GetProperty("responses").GetProperty("409").GetProperty("description").GetString();
        Assert.Contains("incident-action-state-conflict", resolveConflict, StringComparison.Ordinal);
        Assert.Contains("incident-manual-resolution-state-conflict", resolveConflict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandRoleDenialBodyIsNotDocumentedAsAnEndpointProblemExtension()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WebApplicationFactory<Program>().WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/11111111-1111-1111-1111-111111111111/acknowledge");
        request.Headers.Add("X-EE-Pulse-Role", "Viewer");
        request.Headers.Add("X-EE-Pulse-Actor", "22222222-2222-2222-2222-222222222222");
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.False(body.RootElement.TryGetProperty("code", out _));
        Assert.False(body.RootElement.TryGetProperty("correlationId", out _));
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WebApplicationFactory<Program>().WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing());
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string CheckedInArtifactPath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "api", "openapi-v1.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Unable to locate the checked-in OpenAPI artifact.");
    }

    private static string? FindDifference(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind) return path + " (value kind)";
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var expectedProperties = expected.EnumerateObject().ToDictionary(property => property.Name, StringComparer.Ordinal);
            var actualProperties = actual.EnumerateObject().ToDictionary(property => property.Name, StringComparer.Ordinal);
            if (!expectedProperties.Keys.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(actualProperties.Keys.OrderBy(name => name, StringComparer.Ordinal)))
                return path + " (property set)";
            foreach (var (name, value) in expectedProperties)
            {
                if (path == "$" && name == "servers") continue; // Generated from the request host, asserted by the caller.
                var difference = FindDifference(value.Value, actualProperties[name].Value, path + "." + name);
                if (difference is not null) return difference;
            }
            return null;
        }

        if (expected.ValueKind == JsonValueKind.Array)
        {
            var expectedItems = expected.EnumerateArray().ToArray();
            var actualItems = actual.EnumerateArray().ToArray();
            if (expectedItems.Length != actualItems.Length) return path + " (array length)";
            for (var index = 0; index < expectedItems.Length; index++)
            {
                var difference = FindDifference(expectedItems[index], actualItems[index], path + "[" + index + "]");
                if (difference is not null) return difference;
            }
            return null;
        }

        return expected.GetRawText() == actual.GetRawText() ? null : path;
    }

    private static JsonElement Parameter(JsonElement operation, string name) => operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == name);

    private static void AssertStringEnum(JsonElement schema, string[] values)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(values, schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    private static void AssertTimestamp(JsonElement schemas, string schemaName, string propertyName, bool nullable)
    {
        var schema = schemas.GetProperty(schemaName).GetProperty("properties").GetProperty(propertyName);
        if (nullable)
            Assert.Equal(["null", "string"], schema.GetProperty("type").EnumerateArray().Select(value => value.GetString()!).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        else Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("date-time", schema.GetProperty("format").GetString());
        Assert.Equal("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?Z$", schema.GetProperty("pattern").GetString());
    }

    private static void AssertCanonicalUuid(JsonElement schemas, string schemaName, string propertyName)
    {
        var schema = schemas.GetProperty(schemaName).GetProperty("properties").GetProperty(propertyName);
        Assert.Equal("uuid", schema.GetProperty("format").GetString());
        Assert.Equal("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", schema.GetProperty("pattern").GetString());
    }

    private static void AssertOperation(JsonElement paths, string path, string method, string operationId, string successSchema,
        string[] statuses, string[] parameters, string? requestSchema = null)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        Assert.Equal(statuses, operation.GetProperty("responses").EnumerateObject().Select(response => response.Name).ToArray());
        Assert.Contains(operation.GetProperty("security").EnumerateArray(), requirement => requirement.TryGetProperty("Bearer", out _));
        Assert.Equal(parameters, operation.GetProperty("parameters").EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()).ToArray());
        var success = operation.GetProperty("responses").GetProperty(statuses[0]);
        var successBodySchema = success.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        if (successBodySchema.TryGetProperty("$ref", out var schemaRef))
            Assert.Equal("#/components/schemas/" + successSchema, schemaRef.GetString());
        else
            Assert.Equal("#/components/schemas/" + successSchema, successBodySchema.GetProperty("allOf")[0].GetProperty("$ref").GetString());
        if (requestSchema is not null)
            Assert.Equal("#/components/schemas/" + requestSchema, operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        if (operation.GetProperty("responses").TryGetProperty("400", out var badRequest))
            Assert.True(badRequest.GetProperty("content").TryGetProperty("application/problem+json", out _));
        if (parameters.Contains("Idempotency-Key", StringComparer.Ordinal))
        {
            var idempotency = operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
            Assert.True(idempotency.GetProperty("required").GetBoolean());
        }
    }
}
