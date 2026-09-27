using System.Text.Json.Nodes;
using EePulse.Contracts.Dashboard;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EePulse.Api.OpenApi;

/// <summary>
/// Adds the frozen WP-07 dashboard and incident HTTP details that cannot be inferred from
/// handlers which deliberately validate their raw query/header/body inputs themselves.
/// </summary>
public sealed class Wp07DashboardOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    private const string CorrelationPattern = "^[0-9a-f]{32}$";
    private const string UuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
    private const string AcceptedUuidPattern = "^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$";
    private const string IdempotencyKeyPattern = "^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
    private const string CanonicalUtcTimestampPattern = "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?Z$";
    private const string UtcTimestampQueryPattern = "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?Z$";
    private static readonly string[] DashboardStatusTokens = ["Unknown", "Up", "Degraded", "Down", "Recovering", "Maintenance", "Disabled"];
    private static readonly string[] IncidentStatusTokens = ["Open", "Acknowledged", "Resolved"];

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        AddCommandRequestSchemas(document);
        ConfigureResponseSchemas(document);
        ConfigureSummary(Get(document, Wp07DashboardContract.DashboardSummaryPath, HttpMethod.Get));
        ConfigureDeviceStatus(Get(document, Wp07DashboardContract.DeviceStatusPathTemplate, HttpMethod.Get));
        ConfigureIncidentList(Get(document, Wp07DashboardContract.IncidentsPath, HttpMethod.Get));
        ConfigureIncidentDetail(Get(document, Wp07DashboardContract.IncidentDetailPathTemplate, HttpMethod.Get));
        ConfigureDeviceIncidentHistory(Get(document, Wp07DashboardContract.DeviceIncidentHistoryPathTemplate, HttpMethod.Get));
        ConfigureLifecycle(Get(document, Wp07DashboardContract.IncidentLifecycleEventsPathTemplate, HttpMethod.Get));
        ConfigureComments(Get(document, Wp07DashboardContract.IncidentCommentsPathTemplate, HttpMethod.Get));
        ConfigureCommand(document, Get(document, Wp07DashboardContract.IncidentAcknowledgePathTemplate, HttpMethod.Post), "acknowledge");
        ConfigureCommand(document, Get(document, Wp07DashboardContract.IncidentCommentsPathTemplate, HttpMethod.Post), "comment");
        ConfigureCommand(document, Get(document, Wp07DashboardContract.IncidentResolvePathTemplate, HttpMethod.Post), "resolve");
        return Task.CompletedTask;
    }

    private static OpenApiOperation Get(OpenApiDocument document, string path, HttpMethod method) =>
        document.Paths![path].Operations![method];

    private static void ConfigureSummary(OpenApiOperation operation)
    {
        operation.Description += " Requires the dashboard.read policy (Viewer, Operator, Engineer, Administrator, or Auditor).";
        AddFilterParameters(operation, includeStatus: true);
        AddHeader(operation, "If-None-Match", false, "Conditional GET entity-tag list; a matching current strong ETag returns 304.");
        AddHeader(operation, "If-Match", false, "Unsupported for this read; any supplied value returns 400 if-match-unsupported.");
        AddCommonResponseHeaders(operation, ["200", "304"], cacheControl: true);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-dashboard-filter, if-match-unsupported, or invalid-if-none-match",
            ["503"] = "dashboard-summary-unavailable"
        });
    }

    private static void ConfigureDeviceStatus(OpenApiOperation operation)
    {
        operation.Description += " Requires the dashboard.read policy (Viewer, Operator, Engineer, Administrator, or Auditor). This route accepts no query parameters.";
        DescribePathId(operation, "Canonical lowercase UUID-D Device identifier.");
        AddHeader(operation, "If-None-Match", false, "Conditional GET entity-tag list; a matching current strong ETag returns 304.");
        AddHeader(operation, "If-Match", false, "Unsupported for this read; any supplied value returns 400 if-match-unsupported.");
        AddCommonResponseHeaders(operation, ["200", "304"], cacheControl: true);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-device-status-id, invalid-device-status-query, if-match-unsupported, or invalid-if-none-match",
            ["503"] = "device-status-unavailable"
        });
    }

    private static void ConfigureIncidentList(OpenApiOperation operation)
    {
        operation.Description += " Requires incidents.read (Viewer, Operator, Engineer, Administrator, or Auditor).";
        AddIncidentFilterParameters(operation, false);
        AddHeader(operation, "If-Match", false, "Unsupported for this read; any supplied value returns 400 if-match-unsupported.");
        operation.Description += " If-None-Match is intentionally ignored for this collection and it never returns 304.";
        AddCommonResponseHeaders(operation, []);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-incident-query, if-match-unsupported, invalid-cursor, or cursor-filter-mismatch",
            ["503"] = "incident-read-unavailable"
        });
    }

    private static void ConfigureIncidentDetail(OpenApiOperation operation)
    {
        operation.Description += " Requires incidents.read (Viewer, Operator, Engineer, Administrator, or Auditor). This route accepts no query parameters.";
        DescribePathId(operation, "Canonical lowercase UUID-D incident identifier.");
        AddHeader(operation, "If-Match", false, "Unsupported for this read; any supplied value returns 400 if-match-unsupported.");
        AddHeader(operation, "If-None-Match", false, "Bounded entity-tag list using weak comparison; wildcard behavior is supported. Malformed input and every empty list member return 400 invalid-if-none-match; a match returns a bodyless 304.");
        AddCommonResponseHeaders(operation, ["200", "304"], cacheControl: true);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-incident-query, invalid-incident-id, if-match-unsupported, or invalid-if-none-match",
            ["404"] = "incident-not-found",
            ["503"] = "incident-read-unavailable"
        });
    }

    private static void ConfigureDeviceIncidentHistory(OpenApiOperation operation)
    {
        operation.Description += " Requires incidents.read (Viewer, Operator, Engineer, Administrator, or Auditor).";
        DescribePathId(operation, "Canonical lowercase UUID-D Device identifier.");
        AddIncidentFilterParameters(operation, true);
        AddHeader(operation, "If-Match", false, "Unsupported for this read; any supplied value returns 400 if-match-unsupported.");
        operation.Description += " If-None-Match is intentionally ignored for this collection and it never returns 304.";
        AddCommonResponseHeaders(operation, []);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-incident-query, invalid-device-id, if-match-unsupported, invalid-cursor, or cursor-filter-mismatch",
            ["404"] = "device-not-found",
            ["503"] = "incident-read-unavailable"
        });
    }

    private static void ConfigureLifecycle(OpenApiOperation operation)
    {
        operation.Description += " Requires incidents.read (Viewer, Operator, Engineer, Administrator, or Auditor). It has no aggregate ETag and never returns 304.";
        DescribePathId(operation, "Canonical lowercase UUID-D incident identifier.");
        AddCursorParameters(operation, "OccurredAtDesc (default) or OccurredAtAsc.", ["OccurredAtDesc", "OccurredAtAsc"]);
        AddCommonResponseHeaders(operation, []);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-incident-query, invalid-incident-id, invalid-cursor, or cursor-filter-mismatch",
            ["404"] = "incident-not-found",
            ["503"] = "incident-read-unavailable"
        });
    }

    private static void ConfigureComments(OpenApiOperation operation)
    {
        operation.Description += " Requires incidents.read (Viewer, Operator, Engineer, Administrator, or Auditor). It has no aggregate ETag and never returns 304.";
        DescribePathId(operation, "Canonical lowercase UUID-D incident identifier.");
        AddCursorParameters(operation, "CreatedAtDesc (default) or CreatedAtAsc.", ["CreatedAtDesc", "CreatedAtAsc"]);
        AddCommonResponseHeaders(operation, []);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-incident-query, invalid-incident-id, invalid-cursor, or cursor-filter-mismatch",
            ["404"] = "incident-not-found",
            ["503"] = "incident-read-unavailable"
        });
    }

    private static void ConfigureCommand(OpenApiDocument document, OpenApiOperation operation, string kind)
    {
        operation.Description += " Requires incidents.operate (Operator or Administrator). Query parameters are forbidden. The request body accepts application/json case-insensitively with valid media-type parameters and has no additional JSON properties; unsupported media types return 400 invalid-content-type.";
        DescribePathId(operation, "Canonical lowercase UUID-D incident identifier.");
        AddCommandRequestBody(document, operation, kind == "resolve" ? "ResolveIncidentRequest" :
            kind == "acknowledge" ? "AcknowledgeIncidentRequest" : "AddIncidentCommentRequest");
        AddHeader(operation, Wp07DashboardContract.IdempotencyKeyHeader, true,
            "Required single canonical lowercase UUID-D key other than all-zero. It makes retries replay the original response.", "uuid", IdempotencyKeyPattern);
        AddHeader(operation, "If-Match", true,
            "Required single current strong opaque incident ETag. Missing is 428; empty or OWS-only fields, malformed, weak, wildcard, comma-list, or repeated values are 400. The quoted empty strong opaque tag \"\" is syntactically valid and is evaluated as current or stale.");
        AddCommonResponseHeaders(operation, kind == "comment" ? ["201", "412"] : ["200", "412"]);
        DescribeSuccessEtag(operation, kind == "comment" ? "201" : "200");
        var conflicts = kind == "resolve"
            ? "idempotency-key-reuse-conflict, incident-action-state-conflict, or incident-manual-resolution-state-conflict (the latter has currentEtag, incidentId, probeId, underlyingStatus, visibleStatus, and stateVersion extensions)"
            : kind == "acknowledge"
                ? "idempotency-key-reuse-conflict or incident-action-state-conflict (with currentEtag extension)"
                : "idempotency-key-reuse-conflict";
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-incident-query, invalid-incident-id, invalid-idempotency-key, invalid-if-match, invalid-content-type, invalid-json, or invalid-incident-text",
            ["403"] = "Authorization middleware role denial returns the framework Problem Details body without a stable code or correlationId extension; after authorization succeeds, an invalid incident actor identity returns invalid-incident-actor-identity with the endpoint's correlationId extension",
            ["404"] = "incident-not-found",
            ["409"] = conflicts,
            ["412"] = "concurrency-conflict with currentEtag extension",
            ["428"] = "precondition-required",
            ["503"] = "incident-command-unavailable"
        });
        if (kind is "acknowledge" or "resolve")
        {
            var conflict = (OpenApiResponse)operation.Responses!["409"];
            conflict.Headers ??= new Dictionary<string, IOpenApiHeader>();
            conflict.Headers["ETag"] = new OpenApiHeader
            {
                Description = "Optional current strong incident tag. Present only for incident state-conflict outcomes (incident-action-state-conflict or incident-manual-resolution-state-conflict); absent for idempotency-key-reuse-conflict.",
                Schema = StringSchema(pattern: "^\\\"[\\x21\\x23-\\x7E]+\\\"$")
            };
            conflict.Description += " State-conflict responses include the current ETag; idempotency-key-reuse-conflict does not.";
            if (kind == "acknowledge") ConstrainActionStatus(operation, "Acknowledged");
            else ConstrainActionStatus(operation, "Resolved");
        }
    }

    private static void ConstrainActionStatus(OpenApiOperation operation, string status)
    {
        var successStatus = status == "Acknowledged" ? "200" : "200";
        if (operation.Responses is null || operation.Responses[successStatus] is not OpenApiResponse response ||
            response.Content is null || !response.Content.TryGetValue("application/json", out var mediaType) || mediaType.Schema is not OpenApiSchemaReference reference)
            throw new InvalidOperationException("Incident action response schema is unavailable.");

        mediaType.Schema = new OpenApiSchema
        {
            AllOf =
            [
                reference,
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                    {
                        ["status"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Enum = [JsonValue.Create(status)]
                        }
                    }
                }
            ]
        };
    }

    private static void AddCommandRequestSchemas(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        AddCommandRequestSchema(document, "AcknowledgeIncidentRequest", "comment");
        AddCommandRequestSchema(document, "AddIncidentCommentRequest", "comment");
        AddCommandRequestSchema(document, "ResolveIncidentRequest", "note");
    }

    private static void ConfigureResponseSchemas(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("OpenAPI components are unavailable.");
        ConfigureStringEnum(schemas, "DashboardProbeStatus", DashboardStatusTokens);
        ConfigureStringEnum(schemas, "IncidentStatus", IncidentStatusTokens);

        foreach (var (schemaName, propertyName) in new[]
        {
            ("DashboardRecentDownItem", "sinceAt"), ("DashboardOfflineAgentItem", "lastHeartbeatAt"),
            ("DashboardOfflineAgentItem", "lastReportedAt"), ("DashboardOpenIncidentItem", "openedAt"),
            ("DeviceStatusResponse", "lastFreshEventAt"), ("DeviceStatusResponse", "lastReceivedAt"),
            ("IncidentResponse", "openedAt"), ("IncidentResponse", "acknowledgedAt"),
            ("IncidentResponse", "resolvedAt"), ("IncidentLifecycleResponse", "occurredAt"),
            ("IncidentActionResponse", "completedAt"), ("IncidentCommentResponse", "createdAt")
        })
        {
            ConfigureTimestamp(Property(schemas, schemaName, propertyName), IsNullableTimestamp(schemas, schemaName, propertyName));
        }

        foreach (var (schemaName, propertyName) in new[]
        {
            ("DashboardRecentDownItem", "deviceId"), ("DashboardRecentDownItem", "probeId"), ("DashboardRecentDownItem", "siteId"), ("DashboardRecentDownItem", "openIncidentId"),
            ("DashboardOfflineAgentItem", "agentId"), ("DashboardOfflineAgentItem", "agentGroupId"),
            ("DashboardOpenIncidentItem", "incidentId"), ("DashboardOpenIncidentItem", "deviceId"), ("DashboardOpenIncidentItem", "probeId"), ("DashboardOpenIncidentItem", "siteId"),
            ("StatusEnrichedDeviceResponse", "id"), ("StatusEnrichedDeviceResponse", "siteId"),
            ("DeviceStatusResponse", "probeId"), ("DeviceStatusResponse", "agentId"), ("DeviceStatusResponse", "openIncidentId"),
            ("IncidentResponse", "id"), ("IncidentResponse", "probeId"), ("IncidentResponse", "deviceId"), ("IncidentResponse", "siteId"), ("IncidentResponse", "acknowledgedBy"), ("IncidentResponse", "resolvedBy"),
            ("IncidentLifecycleResponse", "eventId"), ("IncidentLifecycleResponse", "incidentId"), ("IncidentLifecycleResponse", "actorId"),
            ("IncidentActionResponse", "incidentId"), ("IncidentCommentResponse", "id"), ("IncidentCommentResponse", "incidentId"), ("IncidentCommentResponse", "authorId")
        }) ConfigureCanonicalUuid(Property(schemas, schemaName, propertyName));

        foreach (var schemaName in new[] { "CursorPageOfIncidentResponse", "CursorPageOfIncidentLifecycleResponse", "CursorPageOfIncidentCommentResponse" })
            ConfigureBoundedString(Property(schemas, schemaName, "nextCursor"), 1, Wp07DashboardContract.MaximumCursorLength);
        foreach (var schemaName in new[] { "CursorPageOfIncidentResponse", "CursorPageOfIncidentLifecycleResponse", "CursorPageOfIncidentCommentResponse" })
            ConfigureInteger(Property(schemas, schemaName, "pageSize"), 1, Wp07DashboardContract.MaximumPageSize);

        ConfigureInteger(Property(schemas, "DashboardSummaryResponse", "schemaVersion"), Wp07DashboardContract.SchemaVersion, Wp07DashboardContract.SchemaVersion);
        ConfigureInteger(Property(schemas, "DashboardStatusCount", "count"), 0, null);
        ConfigureInteger(Property(schemas, "DashboardOpenIncidentItem", "occurrenceCount"), 0, null);
        ConfigureInteger(Property(schemas, "StatusEnrichedDeviceResponse", "rowVersion"), 0, null);
        ConfigureInteger(Property(schemas, "DeviceStatusResponse", "stateVersion"), 0, null);
        ConfigureInteger(Property(schemas, "IncidentResponse", "occurrenceCount"), 1, null);
        ConfigureInteger(Property(schemas, "IncidentResponse", "totalDowntimeSeconds"), 0, long.MaxValue);

        foreach (var schemaName in new[] { "DashboardSummaryResponse" })
        {
            ConfigureArray(Property(schemas, schemaName, "recentlyDown"), 0, Wp07DashboardContract.DashboardSummaryListMaximum);
            ConfigureArray(Property(schemas, schemaName, "offlineAgents"), 0, Wp07DashboardContract.DashboardSummaryListMaximum);
            ConfigureArray(Property(schemas, schemaName, "openIncidents"), 0, Wp07DashboardContract.DashboardSummaryListMaximum);
            ConfigureArray(Property(schemas, schemaName, "statusCounts"), DashboardStatusTokens.Length, DashboardStatusTokens.Length);
        }

        ConfigureStringEnum(Property(schemas, "DashboardOfflineAgentItem", "status"), ["Offline"]);
        ConfigureCanonicalUuid(Property(schemas, "DashboardFilter", "siteId"));
        Property(schemas, "DashboardFilter", "siteId").MinLength = 1;
        var recentDown = (OpenApiSchema)schemas["DashboardRecentDownItem"];
        recentDown.Required ??= new HashSet<string>(StringComparer.Ordinal);
        recentDown.Required.Add("openIncidentId");
        Property(schemas, "DashboardRecentDownItem", "openIncidentId").Type = JsonSchemaType.String;
        foreach (var name in new[] { "area", "deviceType", "criticality", "tag" })
        {
            var filter = Property(schemas, "DashboardFilter", name);
            filter.MinLength = 1;
            filter.MaxLength = null;
            filter.Description = "When present, trimmed and NFC-normalized; non-empty and at most 128 UTF-16 code units. No raw-input maxLength is published.";
        }
        ConfigureStringEnumProperty(schemas, "DashboardOpenIncidentItem", "status", ["Open", "Acknowledged"]);
        var statusCounts = Property(schemas, "DashboardSummaryResponse", "statusCounts");
        statusCounts.Description = "Exactly seven counts in fixed order: Unknown, Up, Degraded, Down, Recovering, Maintenance, Disabled.";
        var lifecycle = (OpenApiSchema)schemas["IncidentLifecycleResponse"];
        lifecycle.Description = "Runtime mappings: opened/failure-threshold-met, resolved/recovery-threshold-met, and occurrence/recovery-failed have null actorId and comment; acknowledged/operator-acknowledgement and manually-resolved/manual-resolution require a non-null actorId and non-empty comment.";
        ConfigureStringEnum(Property(schemas, "IncidentLifecycleResponse", "type"), ["opened", "resolved", "occurrence", "acknowledged", "manually-resolved"]);
        ConfigureStringEnum(Property(schemas, "IncidentLifecycleResponse", "reasonCode"), ["failure-threshold-met", "recovery-threshold-met", "recovery-failed", "operator-acknowledgement", "manual-resolution"]);
        lifecycle.OneOf =
        [
            LifecycleVariant("opened", "failure-threshold-met", actorRequired: false),
            LifecycleVariant("resolved", "recovery-threshold-met", actorRequired: false),
            LifecycleVariant("occurrence", "recovery-failed", actorRequired: false),
            LifecycleVariant("acknowledged", "operator-acknowledgement", actorRequired: true),
            LifecycleVariant("manually-resolved", "manual-resolution", actorRequired: true)
        ];
        foreach (var (schemaName, propertyName) in new[]
        {
            ("IncidentResponse", "acknowledgementComment"), ("IncidentResponse", "resolutionNote"), ("IncidentLifecycleResponse", "comment"),
            ("IncidentCommentResponse", "comment")
        })
        {
            var schema = Property(schemas, schemaName, propertyName);
            ConfigureUtf16BoundedString(schema, propertyName == "comment" && schemaName == "IncidentCommentResponse" ? 1 : 0,
                Wp07DashboardContract.MaximumCommentLength);
        }
    }

    private static OpenApiSchema LifecycleVariant(string type, string reason, bool actorRequired)
    {
        var actor = actorRequired
            ? new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Pattern = UuidPattern }
            : new OpenApiSchema { Type = JsonSchemaType.Null };
        var comment = actorRequired
            ? new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, Description = "Non-empty trimmed command text, limited to 2,000 UTF-16 code units." }
            : new OpenApiSchema { Type = JsonSchemaType.Null };
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string>(["type", "reasonCode", "actorId", "comment"], StringComparer.Ordinal),
            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(type)] },
                ["reasonCode"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(reason)] },
                ["actorId"] = actor,
                ["comment"] = comment
            }
        };
    }

    private static OpenApiSchema Property(IDictionary<string, IOpenApiSchema> schemas, string schemaName, string propertyName) =>
        (OpenApiSchema)((OpenApiSchema)schemas[schemaName]).Properties![propertyName];

    private static bool IsNullableTimestamp(IDictionary<string, IOpenApiSchema> schemas, string schemaName, string propertyName) =>
        schemaName is "DashboardOfflineAgentItem" or "DeviceStatusResponse" ||
        schemaName == "IncidentResponse" && propertyName is "acknowledgedAt" or "resolvedAt";

    private static void ConfigureStringEnum(IDictionary<string, IOpenApiSchema> schemas, string schemaName, IReadOnlyList<string> values) =>
        ConfigureStringEnum((OpenApiSchema)schemas[schemaName], values);

    private static void ConfigureStringEnumProperty(IDictionary<string, IOpenApiSchema> schemas, string schemaName, string propertyName, IReadOnlyList<string> values)
    {
        var schema = (OpenApiSchema)schemas[schemaName];
        var property = new OpenApiSchema();
        ConfigureStringEnum(property, values);
        schema.Properties![propertyName] = property;
    }

    private static void ConfigureStringEnum(OpenApiSchema schema, IReadOnlyList<string> values)
    {
        schema.Type = JsonSchemaType.String;
        schema.Enum = [.. values.Select(value => JsonValue.Create(value))];
    }

    private static void ConfigureTimestamp(OpenApiSchema schema, bool nullable)
    {
        schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        schema.Format = "date-time";
        schema.Pattern = CanonicalUtcTimestampPattern;
    }

    private static void ConfigureCanonicalUuid(OpenApiSchema schema)
    {
        var nullable = schema.Type is { } type && (type & JsonSchemaType.Null) != 0;
        schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        schema.Format = "uuid";
        schema.Pattern = UuidPattern;
    }

    private static void ConfigureInteger(OpenApiSchema schema, decimal? minimum, decimal? maximum)
    {
        var nullable = schema.Type is { } type && (type & JsonSchemaType.Null) != 0;
        schema.Type = nullable ? JsonSchemaType.Integer | JsonSchemaType.Null : JsonSchemaType.Integer;
        schema.Minimum = minimum?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        schema.Maximum = maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ConfigureBoundedString(OpenApiSchema schema, int? minimumLength, int? maximumLength)
    {
        var nullable = schema.Type is { } type && (type & JsonSchemaType.Null) != 0;
        schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        schema.MinLength = minimumLength;
        schema.MaxLength = maximumLength;
    }

    private static void ConfigureUtf16BoundedString(OpenApiSchema schema, int minimumLength, int maximumLength)
    {
        var nullable = schema.Type is { } type && (type & JsonSchemaType.Null) != 0;
        schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        schema.MinLength = minimumLength > 0 ? minimumLength : null;
        schema.MaxLength = null;
        schema.Description = $"Limited to {minimumLength} through {maximumLength} UTF-16 code units; JSON Schema maxLength is omitted because it counts Unicode scalar values.";
    }

    private static void ConfigureArray(OpenApiSchema schema, int? minimumItems, int? maximumItems)
    {
        schema.MinItems = minimumItems;
        schema.MaxItems = maximumItems;
    }

    private static void AddCommandRequestSchema(OpenApiDocument document, string name, string propertyName)
    {
        document.Components!.Schemas![name] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string>(StringComparer.Ordinal) { propertyName },
            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                [propertyName] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    MinLength = 1,
                    Description = "Raw text must be 1 through 2,000 UTF-16 code units (OpenAPI maxLength is omitted because it counts Unicode scalar values); the persisted value is trimmed and rejects controls and Unicode line separators."
                }
            }
        };
    }

    private static void AddCommandRequestBody(OpenApiDocument document, OpenApiOperation operation, string schemaName)
    {
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "Strict JSON request body; only the documented property is accepted.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType { Schema = new OpenApiSchemaReference(schemaName, document) }
            }
        };
    }

    private static void AddFilterParameters(OpenApiOperation operation, bool includeStatus)
    {
        AddQuery(operation, "siteId", "Optional UUID-D Site filter; uppercase and lowercase hexadecimal are accepted and normalized.", "uuid", AcceptedUuidPattern, minimumLength: 1);
        AddQuery(operation, "area", "Trimmed and NFC-normalized before validation; the normalized value must be non-empty and at most 128 UTF-16 code units. No raw-input maxLength is published.", minimumLength: 1);
        AddQuery(operation, "deviceType", "Trimmed and NFC-normalized before validation; the normalized value must be non-empty and at most 128 UTF-16 code units. No raw-input maxLength is published.", minimumLength: 1);
        AddQuery(operation, "criticality", "Trimmed and NFC-normalized before validation; the normalized value must be non-empty and at most 128 UTF-16 code units. No raw-input maxLength is published.", minimumLength: 1);
        AddQuery(operation, "tag", "Trimmed and NFC-normalized before validation; the normalized value must be non-empty and at most 128 UTF-16 code units. No raw-input maxLength is published.", minimumLength: 1);
        if (includeStatus) AddQuery(operation, "status", "Optional exact visible status.", minimumLength: 1, enumValues: DashboardStatusTokens);
    }

    private static void AddIncidentFilterParameters(OpenApiOperation operation, bool deviceHistory)
    {
        if (!deviceHistory)
        {
            AddQuery(operation, "siteId", "Optional canonical lowercase UUID-D Site filter.", "uuid", UuidPattern);
            AddQuery(operation, "deviceId", "Optional canonical lowercase UUID-D Device filter.", "uuid", UuidPattern);
            AddQuery(operation, "probeId", "Optional canonical lowercase UUID-D Probe filter.", "uuid", UuidPattern);
        }
        AddQuery(operation, "status", "Optional exact incident status.", enumValues: IncidentStatusTokens);
        AddQuery(operation, "openedFrom", "Optional inclusive UTC instant in yyyy-MM-ddTHH:mm:ss[.FFFFFFF]Z form.", "date-time", UtcTimestampQueryPattern);
        AddQuery(operation, "openedTo", "Optional inclusive UTC instant in yyyy-MM-ddTHH:mm:ss[.FFFFFFF]Z form; it must not precede openedFrom.", "date-time", UtcTimestampQueryPattern);
        AddCursorParameters(operation, "OpenedAtDesc (default) or OpenedAtAsc.", ["OpenedAtDesc", "OpenedAtAsc"]);
    }

    private static void AddCursorParameters(OpenApiOperation operation, string sortDescription, IReadOnlyList<string>? sortValues = null)
    {
        AddQuery(operation, "sort", sortDescription, enumValues: sortValues);
        AddQuery(operation, "cursor", "Optional opaque protected cursor token, maximum 2,048 characters. Omission starts the query; an explicitly empty value is invalid. It is bound to this route, resource, filters, and sort.", minimumLength: 1, maximumLength: Wp07DashboardContract.MaximumCursorLength);
        AddQuery(operation, "pageSize", "Optional page size. Default is 50; accepted range is 1 through 200.", "int32", minimum: 1, maximum: 200, defaultValue: 50);
    }

    private static void DescribePathId(OpenApiOperation operation, string description)
    {
        operation.Parameters ??= [];
        var existing = operation.Parameters.FirstOrDefault(candidate => candidate.In == ParameterLocation.Path && candidate.Name == "id");
        if (existing is not null) operation.Parameters.Remove(existing);
        var parameter = new OpenApiParameter { Name = "id", In = ParameterLocation.Path, Required = true };
        operation.Parameters.Add(parameter);
        parameter.Description = description;
        parameter.Required = true;
        parameter.Schema = StringSchema("uuid", UuidPattern);
    }

    private static void AddQuery(OpenApiOperation operation, string name, string description, string? format = null,
        string? pattern = null, decimal? minimum = null, decimal? maximum = null, int? defaultValue = null,
        int? minimumLength = null, int? maximumLength = null, IReadOnlyList<string>? enumValues = null) =>
        AddParameter(operation, name, ParameterLocation.Query, false, description,
            StringSchema(format, pattern, minimum, maximum, defaultValue, minimumLength, maximumLength, enumValues));

    private static void AddHeader(OpenApiOperation operation, string name, bool required, string description,
        string? format = null, string? pattern = null) =>
        AddParameter(operation, name, ParameterLocation.Header, required, description, StringSchema(format, pattern));

    private static void AddParameter(OpenApiOperation operation, string name, ParameterLocation location, bool required,
        string description, OpenApiSchema schema)
    {
        operation.Parameters ??= [];
        var existing = operation.Parameters.FirstOrDefault(candidate => candidate.In == location && candidate.Name == name);
        if (existing is not null) operation.Parameters.Remove(existing);
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name, In = location, Required = required, Description = description, Schema = schema
        });
    }

    private static OpenApiSchema StringSchema(string? format = null, string? pattern = null, decimal? minimum = null,
        decimal? maximum = null, int? defaultValue = null, int? minimumLength = null, int? maximumLength = null,
        IReadOnlyList<string>? enumValues = null)
    {
        var schema = new OpenApiSchema { Type = format == "int32" ? JsonSchemaType.Integer : JsonSchemaType.String, Format = format, Pattern = pattern };
        if (minimum.HasValue) schema.Minimum = minimum.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (maximum.HasValue) schema.Maximum = maximum.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (minimumLength.HasValue) schema.MinLength = minimumLength;
        if (maximumLength.HasValue) schema.MaxLength = maximumLength;
        if (enumValues is not null) schema.Enum = [.. enumValues.Select(value => JsonValue.Create(value))];
        if (defaultValue.HasValue) schema.Default = JsonValue.Create(defaultValue.Value);
        return schema;
    }

    private static void AddCommonResponseHeaders(OpenApiOperation operation, IReadOnlyList<string> etagStatuses, bool cacheControl = false)
    {
        if (operation.Responses is null) return;
        foreach (var (status, candidate) in operation.Responses)
        {
            if (candidate is not OpenApiResponse response) continue;
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["X-Correlation-ID"] = new OpenApiHeader
            {
                Description = "Server-generated lowercase UUID-N correlation identifier.",
                Schema = StringSchema(pattern: CorrelationPattern)
            };
            if (etagStatuses.Contains(status, StringComparer.Ordinal))
            {
                response.Headers["ETag"] = new OpenApiHeader
                {
                    Description = "Current strong opaque entity tag.",
                    Schema = StringSchema(pattern: "^\\\"[\\x21\\x23-\\x7E]+\\\"$")
                };
            }
            if (cacheControl && status is "200" or "304")
            {
                response.Headers["Cache-Control"] = new OpenApiHeader
                {
                    Description = "private, max-age=0, must-revalidate",
                    Schema = StringSchema()
                };
            }
        }
    }

    private static void DescribeSuccessEtag(OpenApiOperation operation, string status)
    {
        if (operation.Responses is null || !operation.Responses.TryGetValue(status, out var candidate) || candidate is not OpenApiResponse response ||
            response.Headers is null || !response.Headers.TryGetValue("ETag", out var header) || header is not OpenApiHeader etag) return;
        etag.Description = "Fresh result: the resulting current incident ETag. Exact idempotency replay: the stored original response ETag, returned without recomputation.";
    }

    private static void DescribeProblems(OpenApiOperation operation, IReadOnlyDictionary<string, string> codesByStatus)
    {
        if (operation.Responses is null) return;
        foreach (var (status, codes) in codesByStatus)
        {
            if (operation.Responses.TryGetValue(status, out var candidate) && candidate is OpenApiResponse response)
                response.Description = codes.StartsWith("Authorization middleware", StringComparison.Ordinal)
                    ? codes
                    : $"Problem Details with stable code ({codes}) and correlationId extension.";
        }
    }
}
