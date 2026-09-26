using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EePulse.Api.Authorization;
using EePulse.Application.Time;
using EePulse.Contracts.Dashboard;
using EePulse.Domain.Auditing;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Serilog.Context;

namespace EePulse.Api.Dashboard;

internal sealed class IncidentReadMetadata;
internal sealed class IncidentCommandMetadata;

internal static class IncidentEndpoints
{
    private const string CorrelationKey = "WP07.Incident.SafeCorrelationId";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static bool IsUnsafeRequestLog(Serilog.Events.LogEvent logEvent)
    {
        foreach (var name in new[] { "RequestPath", "Path" })
        {
            if (logEvent.Properties.TryGetValue(name, out var value) && value is Serilog.Events.ScalarValue { Value: string path } &&
                (path.StartsWith("/api/v1/incidents", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/api/v1/devices/", StringComparison.OrdinalIgnoreCase) && path.Contains("/incidents", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    internal static void MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Wp07DashboardContract.IncidentsPath, (Delegate)((HttpContext context) => Get(context, null, false)))
            .RequireAuthorization("incidents.read").WithMetadata(new IncidentReadMetadata()).ExcludeFromDescription();
        app.MapGet(Wp07DashboardContract.IncidentDetailPathTemplate, (string id, HttpContext context) => Get(context, id, true))
            .RequireAuthorization("incidents.read").WithMetadata(new IncidentReadMetadata()).ExcludeFromDescription();
        app.MapGet(Wp07DashboardContract.DeviceIncidentHistoryPathTemplate, (string id, HttpContext context) => Get(context, id, false))
            .RequireAuthorization("incidents.read").WithMetadata(new IncidentReadMetadata()).ExcludeFromDescription();
        app.MapGet(Wp07DashboardContract.IncidentLifecycleEventsPathTemplate, (string id, HttpContext context) => GetLifecycle(context, id))
            .RequireAuthorization("incidents.read").WithMetadata(new IncidentReadMetadata()).ExcludeFromDescription();
        app.MapGet(Wp07DashboardContract.IncidentCommentsPathTemplate, (string id, HttpContext context) => GetComments(context, id))
            .RequireAuthorization("incidents.read").WithMetadata(new IncidentReadMetadata()).ExcludeFromDescription();
        app.MapPost(Wp07DashboardContract.IncidentAcknowledgePathTemplate, (string id, HttpContext context) =>
                Command(context, id, IncidentCommandRoute.Acknowledge))
            .RequireAuthorization("incidents.operate").WithMetadata(new IncidentCommandMetadata()).ExcludeFromDescription();
        app.MapPost(Wp07DashboardContract.IncidentCommentsPathTemplate, (string id, HttpContext context) =>
                Command(context, id, IncidentCommandRoute.AddComment))
            .RequireAuthorization("incidents.operate").WithMetadata(new IncidentCommandMetadata()).ExcludeFromDescription();
        app.MapPost(Wp07DashboardContract.IncidentResolvePathTemplate, (string id, HttpContext context) =>
                Command(context, id, IncidentCommandRoute.Resolve))
            .RequireAuthorization("incidents.operate").WithMetadata(new IncidentCommandMetadata()).ExcludeFromDescription();
    }

    internal static IApplicationBuilder UseIncidentCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/v1/incidents") ||
            path.StartsWithSegments("/api/v1/devices") && path.Value!.Contains("/incidents", StringComparison.OrdinalIgnoreCase))
        {
            var correlation = Guid.NewGuid().ToString("N");
            var original = context.TraceIdentifier;
            context.Items[CorrelationKey] = correlation;
            context.TraceIdentifier = correlation;
            context.Response.OnStarting(() => { context.Response.Headers["X-Correlation-ID"] = correlation; return Task.CompletedTask; });
            try { using (LogContext.PushProperty("CorrelationId", correlation)) { await next(context); } }
            finally { context.TraceIdentifier = original; }
        }
        else await next(context);
    });

    private static async Task<IResult> Command(HttpContext context, string id, IncidentCommandRoute route)
    {
        var correlation = (string)context.Items[CorrelationKey]!;
        var identities = context.RequestServices.GetRequiredService<PrincipalIdentityResolver>();
        if (!identities.TryResolve(context.User, out var identity) || identity is null)
            return CommandProblem(correlation, "invalid-incident-actor-identity", 403, "Incident actor identity is invalid");
        if (context.Request.QueryString.HasValue || context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Contains('?') == true)
            return Problem(correlation, "invalid-incident-query", 400);
        if (!CanonicalId(id, out var incidentId)) return Problem(correlation, "invalid-incident-id", 400);
        if (!TryIdempotencyKey(context.Request.Headers, out var idempotencyKey)) return Problem(correlation, "invalid-idempotency-key", 400);
        if (!TryIfMatch(context.Request.Headers, out var ifMatch, out var ifMatchStatus))
            return Problem(correlation, ifMatchStatus == IncidentIfMatchClassification.Missing ? "precondition-required" : "invalid-if-match",
                ifMatchStatus == IncidentIfMatchClassification.Missing ? 428 : 400);
        if (!IsJsonContentType(context.Request.ContentType)) return Problem(correlation, "invalid-content-type", 400);

        string rawText;
        try { rawText = await ReadCommandTextAsync(context.Request, route, context.RequestAborted); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); return Results.Empty; }
        catch (InvalidCommandTextException) { return Problem(correlation, "invalid-incident-text", 400); }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException or InvalidDataException or IOException)
        { return CommandProblem(correlation, "invalid-json", 400, "invalid-json", "The request body is invalid."); }
        if (!TryNormalizeCommandText(rawText, out var text)) return Problem(correlation, "invalid-incident-text", 400);
        if (incidentId == Guid.Empty) return Problem(correlation, "incident-not-found", 404);

        try
        {
            return route switch
            {
                IncidentCommandRoute.Acknowledge => await ExecuteCommand(context, incidentId, idempotencyKey, identity, ifMatch, text,
                    new AcknowledgeHandler(), route),
                IncidentCommandRoute.AddComment => await ExecuteCommand(context, incidentId, idempotencyKey, identity, ifMatch, text,
                    new CommentHandler(), route),
                IncidentCommandRoute.Resolve => await ExecuteCommand(context, incidentId, idempotencyKey, identity, ifMatch, text,
                    new ResolveHandler(), route),
                _ => throw new ArgumentOutOfRangeException(nameof(route)),
            };
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); return Results.Empty; }
        catch (Exception exception) when (exception is DbException or DbUpdateException or InvalidOperationException or ArgumentException or JsonException)
        {
            var activity = Activity.Current;
            try { Activity.Current = null; using (LogContext.Suspend()) context.RequestServices.GetRequiredService<Serilog.ILogger>()
                .Error("Incident command request {CorrelationId} failed.", correlation); }
            finally { Activity.Current = activity; }
            return CommandProblem(correlation, "incident-command-unavailable", 503, "Incident command is unavailable");
        }
    }

    private static async Task<IResult> ExecuteCommand(HttpContext context, Guid incidentId, string idempotencyKey,
        PrincipalIdentity identity, string ifMatch, string text, IIncidentLockedCommandHandler<string, CommandConflict> handler,
        IncidentCommandRoute route)
    {
        var canonical = CanonicalCommandBody(route, text);
        var input = new IncidentCommandInput<string>(route, incidentId, idempotencyKey, identity, ifMatch, canonical,
            (string)context.Items[CorrelationKey]!, text);
        var coordinator = context.RequestServices.GetRequiredService<IIncidentCommandCoordinator>();
        var result = await coordinator.ExecuteAsync(input, handler, context.RequestAborted);
        var correlation = (string)context.Items[CorrelationKey]!;
        return result switch
        {
            IncidentCommandResult<CommandConflict>.Replay replay => Response(replay.Response),
            IncidentCommandResult<CommandConflict>.FreshSuccess success => Response(success.Response),
            IncidentCommandResult<CommandConflict>.NotFound => Problem(correlation, "incident-not-found", 404),
            IncidentCommandResult<CommandConflict>.IdempotencyReuseConflict => Problem(correlation, "idempotency-key-reuse-conflict", 409),
            IncidentCommandResult<CommandConflict>.FreshPreconditionFailed stale => ConcurrencyProblem(correlation, stale.CurrentEtag),
            IncidentCommandResult<CommandConflict>.FreshStateConflict conflict => StateConflict(correlation, conflict.Conflict, conflict.CurrentEtag),
            IncidentCommandResult<CommandConflict>.IndeterminateCommit => CommandProblem(correlation, "incident-command-unavailable", 503, "Incident command is unavailable"),
            _ => CommandProblem(correlation, "incident-command-unavailable", 503, "Incident command is unavailable"),
        };
    }

    private static StoredResponseResult Response(StoredIncidentHttpResponse response) => new(response);

    private static HeaderResult ConcurrencyProblem(string correlation, string etag) => new(
        Results.Json(new { type = "about:blank", title = "concurrency-conflict", status = 412, code = "concurrency-conflict",
            correlationId = correlation, currentEtag = etag }, statusCode: 412, contentType: "application/problem+json"), "ETag", etag);

    private static HeaderResult StateConflict(string correlation, CommandConflict conflict, string etag)
    {
        if (conflict is CommandConflict.ManualResolution manual)
            return new HeaderResult(Results.Json(new { type = "about:blank", title = "Manual resolution is not currently allowed",
                status = 409, code = "incident-manual-resolution-state-conflict", correlationId = correlation, currentEtag = etag,
                incidentId = manual.IncidentId.ToString("D"), probeId = manual.ProbeId.ToString("D"), underlyingStatus = manual.UnderlyingStatus,
                visibleStatus = manual.VisibleStatus, stateVersion = manual.StateVersion }, statusCode: 409, contentType: "application/problem+json"), "ETag", etag);
        return new HeaderResult(Results.Json(new { type = "about:blank", title = "incident-action-state-conflict", status = 409,
            code = "incident-action-state-conflict", correlationId = correlation, currentEtag = etag }, statusCode: 409,
            contentType: "application/problem+json"), "ETag", etag);
    }

    private static IResult CommandProblem(string correlation, string code, int status, string title, string? detail = null) => Results.Json(new
    {
        type = "about:blank", title, status, code, correlationId = correlation, detail
    }, statusCode: status, contentType: "application/problem+json");

    private static bool TryIdempotencyKey(IHeaderDictionary headers, out string key)
    {
        key = string.Empty;
        if (!headers.TryGetValue(Wp07DashboardContract.IdempotencyKeyHeader, out var values) || values.Count != 1) return false;
        var candidate = values[0];
        return candidate is not null && Guid.TryParseExact(candidate, "D", out var parsed) && parsed != Guid.Empty &&
            string.Equals(candidate, parsed.ToString("D"), StringComparison.Ordinal) && (key = candidate) is not null;
    }

    private static bool TryIfMatch(IHeaderDictionary headers, out string etag, out IncidentIfMatchClassification status)
    {
        etag = string.Empty;
        status = IncidentConcurrencyContract.ClassifyIfMatch(headers.IfMatch, "\"validation\"");
        if (status is IncidentIfMatchClassification.Missing or IncidentIfMatchClassification.Invalid) return false;
        etag = headers[HeaderNames.IfMatch].ToString().Trim();
        return true;
    }

    private static bool IsJsonContentType(string? value) => value is not null &&
        MediaTypeHeaderValue.TryParse(value, out var mediaType) && string.Equals(mediaType.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);

    internal static async Task<string> ReadCommandTextAsync(HttpRequest request, IncidentCommandRoute route, CancellationToken ct)
    {
        const int maxBytes = 16 * 1024;
        if (request.ContentLength is > maxBytes) throw new InvalidDataException();

        // Do not use CopyToAsync here: a chunked request has no trustworthy Content-Length,
        // and CopyToAsync can buffer an arbitrarily large body before the limit is observed.
        var bytes = new byte[maxBytes];
        var count = 0;
        while (count < maxBytes)
        {
            var read = await request.Body.ReadAsync(bytes.AsMemory(count, Math.Min(4096, maxBytes - count)), ct);
            if (read == 0) break;
            count += read;
        }

        if (count == maxBytes)
        {
            var overflowProbe = new byte[1];
            if (await request.Body.ReadAsync(overflowProbe, ct) != 0) throw new InvalidDataException();
        }

        var json = new UTF8Encoding(false, true).GetString(bytes, 0, count);
        ValidateJsonSurrogates(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();

        // The request wire format is the repository's Web JSON convention.  The
        // canonical fingerprint representation remains separately specified by ADR-013.
        var expectedProperty = route == IncidentCommandRoute.Resolve ? "note" : "comment";
        JsonElement value = default;
        var found = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, expectedProperty, StringComparison.Ordinal) || found)
                throw new JsonException();
            value = property.Value;
            found = true;
        }

        if (!found || value.ValueKind != JsonValueKind.String) throw new InvalidCommandTextException();
        var text = value.GetString();
        if (text is null) throw new InvalidCommandTextException();
        return text;
    }

    private static void ValidateJsonSurrogates(string json)
    {
        for (var index = 0; index < json.Length; index++)
        {
            if (json[index] != '\\') continue;
            if (++index == json.Length) throw new JsonException();
            if (json[index] != 'u') continue;
            if (index + 4 >= json.Length || !ushort.TryParse(json.AsSpan(index + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) throw new JsonException();
            index += 4;
            if (code is >= 0xD800 and <= 0xDBFF)
            {
                if (index + 6 >= json.Length || json[index + 1] != '\\' || json[index + 2] != 'u' ||
                    !ushort.TryParse(json.AsSpan(index + 3, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low) || low is < 0xDC00 or > 0xDFFF)
                    throw new JsonException();
                index += 6;
            }
            else if (code is >= 0xDC00 and <= 0xDFFF) throw new JsonException();
        }
    }

    private static bool TryNormalizeCommandText(string raw, out string normalized)
    {
        normalized = raw.Trim();
        if (raw.Length is < 1 or > Wp07DashboardContract.MaximumCommentLength || normalized.Length == 0) return false;
        for (var index = 0; index < normalized.Length;)
        {
            if (!System.Text.Rune.DecodeFromUtf16(normalized.AsSpan(index), out var rune, out var consumed).Equals(System.Buffers.OperationStatus.Done)) return false;
            if (rune.Value is <= 0x1F or >= 0x7F and <= 0x9F or 0x2028 or 0x2029) return false;
            index += consumed;
        }
        return true;
    }

    internal static byte[] CanonicalCommandBody(IncidentCommandRoute route, string text)
    {
        var property = route == IncidentCommandRoute.Resolve ? "Note" : "Comment";
        var writer = new ArrayBufferWriter<byte>();
        WriteAscii(writer, "{\"");
        WriteAscii(writer, property);
        WriteAscii(writer, "\":\"");
        for (var index = 0; index < text.Length;)
        {
            if (System.Text.Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
                throw new ArgumentException("Command text must contain valid Unicode scalars.", nameof(text));
            index += consumed;
            if (rune.Value == '"' || rune.Value == '\\')
            {
                var escaped = writer.GetSpan(2);
                escaped[0] = (byte)'\\';
                escaped[1] = (byte)rune.Value;
                writer.Advance(2);
            }
            else
            {
                var written = rune.EncodeToUtf8(writer.GetSpan(4));
                writer.Advance(written);
            }
        }
        WriteAscii(writer, "\"}");
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteAscii(ArrayBufferWriter<byte> writer, string value)
    {
        var target = writer.GetSpan(value.Length);
        for (var index = 0; index < value.Length; index++) target[index] = checked((byte)value[index]);
        writer.Advance(value.Length);
    }

    private sealed class HeaderResult(IResult inner, string name, string value, int? statusCode = null) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers[name] = value;
            if (statusCode.HasValue) context.Response.StatusCode = statusCode.Value;
            await inner.ExecuteAsync(context);
        }
    }

    private sealed class StoredResponseResult(StoredIncidentHttpResponse response) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            context.Response.Headers.ETag = response.Etag;
            await context.Response.Body.WriteAsync(response.BodyUtf8.AsMemory(), context.RequestAborted);
        }
    }

    private sealed class InvalidCommandTextException : Exception;

    private abstract record CommandConflict
    {
        internal sealed record State : CommandConflict;
        internal sealed record ManualResolution(Guid IncidentId, Guid ProbeId, string UnderlyingStatus, string VisibleStatus,
            long StateVersion) : CommandConflict;
    }

    private sealed class AcknowledgeHandler : IIncidentLockedCommandHandler<string, CommandConflict>
    {
        public ValueTask<LockedCommandDecision<CommandConflict>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            if (context.Incident.Status != AvailabilityIncidentStatus.Open)
                return ValueTask.FromResult<LockedCommandDecision<CommandConflict>>(new LockedCommandDecision<CommandConflict>.StateConflict(new CommandConflict.State()));
            context.Incident.Acknowledge(context.ActorId, context.Command, context.OccurredAt);
            var action = new IncidentLifecycleAction(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId, context.ActorId,
                "acknowledgement", "operator-acknowledgement", context.OccurredAt, context.Command, context.IdempotencyKey);
            context.Stage(action);
            context.Stage(Audit(context, "incident.acknowledged"));
            return ValueTask.FromResult<LockedCommandDecision<CommandConflict>>(new LockedCommandDecision<CommandConflict>.Apply(
                new StagedCommandOutcome(action.EventId, "acknowledgement", context.OccurredAt)));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ActionResponse(context, AvailabilityIncidentStatus.Acknowledged));
    }

    private sealed class CommentHandler : IIncidentLockedCommandHandler<string, CommandConflict>
    {
        private Guid authorId;
        private string? comment;
        private DateTimeOffset createdAt;

        public ValueTask<LockedCommandDecision<CommandConflict>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            var comment = new IncidentComment(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId, context.ActorId,
                context.Command, context.OccurredAt, context.IdempotencyKey);
            context.Stage(comment);
            authorId = context.ActorId;
            this.comment = context.Command;
            createdAt = context.OccurredAt;
            context.Stage(Audit(context, "incident.comment.add"));
            return ValueTask.FromResult<LockedCommandDecision<CommandConflict>>(new LockedCommandDecision<CommandConflict>.Apply(
                new StagedCommandOutcome(comment.Id, "general_comment", context.OccurredAt)));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken)
        {
            var response = new IncidentCommentResponse(outcome.OutcomeId.ToString("D"), context.IncidentId.ToString("D"),
                authorId.ToString("D"), comment ?? throw new InvalidOperationException("Comment state is missing."), createdAt);
            return ValueTask.FromResult(new StoredIncidentHttpResponse(201, CanonicalBytes(response), Wp07DashboardCanonicalizer.ContentType,
                context.ResultingEtag));
        }
    }

    private sealed class ResolveHandler : IIncidentLockedCommandHandler<string, CommandConflict>
    {
        public ValueTask<LockedCommandDecision<CommandConflict>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            if (context.Incident.Status == AvailabilityIncidentStatus.Resolved)
                return ValueTask.FromResult<LockedCommandDecision<CommandConflict>>(new LockedCommandDecision<CommandConflict>.StateConflict(new CommandConflict.State()));
            if (context.Projection.UnderlyingStatus is ProbeStatus.Down or ProbeStatus.Recovering)
                return ValueTask.FromResult<LockedCommandDecision<CommandConflict>>(new LockedCommandDecision<CommandConflict>.StateConflict(
                    new CommandConflict.ManualResolution(context.Incident.Id, context.Incident.ProbeId, context.Projection.UnderlyingStatus.ToString(),
                        context.Projection.VisibleStatus.ToString(), context.Projection.StateVersion)));
            context.Projection.ClearOpenIncidentForManualResolution(context.Incident.Id);
            context.Incident.ResolveManually(context.ActorId, context.Command, context.OccurredAt);
            var action = new IncidentLifecycleAction(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId, context.ActorId,
                "manual_resolution", "manual-resolution", context.OccurredAt, context.Command, context.IdempotencyKey);
            context.Stage(action);
            context.Stage(Audit(context, "incident.manually-resolved"));
            return ValueTask.FromResult<LockedCommandDecision<CommandConflict>>(new LockedCommandDecision<CommandConflict>.Apply(
                new StagedCommandOutcome(action.EventId, "manual_resolution", context.OccurredAt)));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ActionResponse(context, AvailabilityIncidentStatus.Resolved));
    }

    private static AuditEvent Audit(LockedIncidentExecutionContext<string> context, string action) => new(Guid.NewGuid(), context.ActorId,
        action, "AvailabilityIncident", context.Incident.Id, null, "{}", context.TrustedCorrelationId, context.OccurredAt, null);

    private static StoredIncidentHttpResponse ActionResponse(IncidentCommandResponseContext context, AvailabilityIncidentStatus status)
    {
        var response = new IncidentActionResponse(context.IncidentId.ToString("D"), Enum.Parse<IncidentStatus>(status.ToString()), context.CompletedAt);
        return new StoredIncidentHttpResponse(200, CanonicalBytes(response), Wp07DashboardCanonicalizer.ContentType, context.ResultingEtag);
    }

    private static async Task<IResult> Get(HttpContext context, string? id, bool detail)
    {
        var correlation = (string)context.Items[CorrelationKey]!;
        if (!TryQuery(context.Request.Query, id is not null && !detail, out var filter, out var cursor, out var pageSize) ||
            detail && (context.Request.QueryString.HasValue || context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Contains('?') == true))
            return Problem(correlation, "invalid-incident-query", 400);
        Guid resourceId = default;
        if (id is not null && !CanonicalId(id, out resourceId))
            return Problem(correlation, detail ? "invalid-incident-id" : "invalid-device-id", 400);
        if (context.Request.Headers.ContainsKey("If-Match")) return Problem(correlation, "if-match-unsupported", 400);
        var conditional = detail && context.Request.Headers.ContainsKey("If-None-Match")
            ? context.Request.Headers.IfNoneMatch.Select(value => value ?? string.Empty).ToArray() : null;
        if (conditional is not null && !ValidConditional(conditional)) return Problem(correlation, "invalid-if-none-match", 400);

        try
        {
            var ct = context.RequestAborted;
            var protector = context.RequestServices.GetRequiredService<IncidentCursorProtector>();
            var binding = new IncidentCursorBinding(id is null ? "incidents" : "device-incidents", id,
                filter!, Wp07DashboardContract.SchemaVersion);
            IncidentSeek? seek = null;
            if (cursor is not null)
            {
                var validation = protector.Validate(cursor, binding, out seek);
                if (validation != IncidentCursorValidation.Valid)
                    return Problem(correlation, validation == IncidentCursorValidation.Invalid ? "invalid-cursor" : "cursor-filter-mismatch", 400);
            }

            var db = context.RequestServices.GetRequiredService<EePulseDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            if (id is not null && !detail && !await db.Devices.AsNoTracking().AnyAsync(device => device.Id == resourceId, ct))
                return Problem(correlation, "device-not-found", 404);
            var rows = from incident in db.AvailabilityIncidents.AsNoTracking()
                       join probe in db.Probes.AsNoTracking() on incident.ProbeId equals probe.Id
                       join device in db.Devices.AsNoTracking() on probe.DeviceId equals device.Id
                       join site in db.Sites.AsNoTracking() on device.SiteId equals site.Id
                       select new ReadRow { Incident = incident, DeviceId = device.Id, DeviceName = device.Name, SiteId = site.Id, SiteName = site.Name };
            if (detail)
            {
                var row = await rows.SingleOrDefaultAsync(row => row.Incident.Id == resourceId, ct);
                if (row is null)
                {
                    if (await db.AvailabilityIncidents.AsNoTracking().AnyAsync(incident => incident.Id == resourceId, ct))
                        throw new InvalidOperationException("Incident read model is incomplete.");
                    return Problem(correlation, "incident-not-found", 404);
                }
                var bytes = CanonicalBytes(ToResponse(row));
                var etag = context.RequestServices.GetRequiredService<IIncidentEtagProvider>().Create(row.Incident.Id, row.Incident.RowVersion);
                await transaction.CommitAsync(ct);
                context.Response.Headers.ETag = etag;
                context.Response.Headers.CacheControl = Wp07DashboardContract.DashboardCacheControl;
                return IncidentConcurrencyContract.ClassifyIfNoneMatch(conditional, etag) == IncidentIfNoneMatchClassification.Match
                    ? Results.StatusCode(304) : Results.Bytes(bytes, Wp07DashboardCanonicalizer.ContentType);
            }

            if (id is not null) rows = rows.Where(row => row.DeviceId == resourceId);
            if (filter!.SiteId is { } siteValue) { var siteId = Guid.Parse(siteValue); rows = rows.Where(row => row.SiteId == siteId); }
            if (filter.DeviceId is { } deviceValue) { var deviceId = Guid.Parse(deviceValue); rows = rows.Where(row => row.DeviceId == deviceId); }
            if (filter.ProbeId is { } probeValue) { var probeId = Guid.Parse(probeValue); rows = rows.Where(row => row.Incident.ProbeId == probeId); }
            if (filter.Status is { } status) { var storedStatus = Enum.Parse<AvailabilityIncidentStatus>(status.ToString()); rows = rows.Where(row => row.Incident.Status == storedStatus); }
            if (filter.OpenedFrom is { } from) rows = rows.Where(row => row.Incident.OpenedAt >= from);
            if (filter.OpenedTo is { } to) rows = rows.Where(row => row.Incident.OpenedAt <= to);
            var descending = filter.Sort == IncidentSort.OpenedAtDesc;
            if (seek is not null)
            {
                var at = seek.OpenedAt;
                var lastId = seek.IncidentId;
                rows = descending
                    ? rows.Where(row => row.Incident.OpenedAt < at || row.Incident.OpenedAt == at && row.Incident.Id.CompareTo(lastId) < 0)
                    : rows.Where(row => row.Incident.OpenedAt > at || row.Incident.OpenedAt == at && row.Incident.Id.CompareTo(lastId) < 0);
            }
            var page = await (descending ? rows.OrderByDescending(row => row.Incident.OpenedAt) : rows.OrderBy(row => row.Incident.OpenedAt))
                .ThenByDescending(row => row.Incident.Id).Take(pageSize + 1).ToArrayAsync(ct);
            var items = page.Take(pageSize).Select(ToResponse).ToArray();
            string? next = null;
            if (page.Length > pageSize)
            {
                var last = page[pageSize - 1].Incident;
                next = protector.Issue(binding, new IncidentSeek(last.OpenedAt, last.Id));
            }
            var body = CanonicalBytes(new CursorPage<IncidentResponse>(items, next, pageSize));
            await transaction.CommitAsync(ct);
            return Results.Bytes(body, Wp07DashboardCanonicalizer.ContentType);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return Results.Empty;
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or InvalidOperationException or
            ArgumentException or JsonException or DashboardContractValidationException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
        {
            var activity = Activity.Current;
            try
            {
                Activity.Current = null;
                using (LogContext.Suspend())
                    context.RequestServices.GetRequiredService<Serilog.ILogger>().Error("Incident read request {CorrelationId} failed.", correlation);
            }
            finally { Activity.Current = activity; }
            return Problem(correlation, "incident-read-unavailable", 503);
        }
    }

    private sealed class ReadRow
    {
        public required AvailabilityIncident Incident { get; init; }
        public Guid DeviceId { get; init; }
        public required string DeviceName { get; init; }
        public Guid SiteId { get; init; }
        public required string SiteName { get; init; }
    }

    private static async Task<IResult> GetLifecycle(HttpContext context, string id)
    {
        var correlation = (string)context.Items[CorrelationKey]!;
        if (!TryTimelineQuery(context.Request.Query, out var sort, out var cursor, out var pageSize))
            return Problem(correlation, "invalid-incident-query", 400);
        if (!CanonicalId(id, out var incidentId)) return Problem(correlation, "invalid-incident-id", 400);

        try
        {
            var protector = context.RequestServices.GetRequiredService<IncidentCursorProtector>();
            var binding = new IncidentCursorBinding("incident-lifecycle-events", id, null,
                Wp07DashboardContract.SchemaVersion, sort.ToString());
            IncidentSeek? seek = null;
            if (cursor is not null)
            {
                var validation = protector.Validate(cursor, binding, out seek);
                if (validation != IncidentCursorValidation.Valid || seek?.SourceRank is not (0 or 1))
                    return Problem(correlation, validation == IncidentCursorValidation.FilterMismatch ? "cursor-filter-mismatch" : "invalid-cursor", 400);
            }

            var ct = context.RequestAborted;
            var db = context.RequestServices.GetRequiredService<EePulseDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            if (!await db.AvailabilityIncidents.AsNoTracking().AnyAsync(incident => incident.Id == incidentId, ct))
                return Problem(correlation, "incident-not-found", 404);

            IQueryable<LifecycleRow> rows = db.Database.SqlQuery<LifecycleRow>($"""
                SELECT event_id AS "EventId", incident_id AS "IncidentId", occurred_at AS "OccurredAt", 0 AS "SourceRank",
                       lifecycle_event_type AS "EngineType", source_reason_code AS "EngineReason", NULL::uuid AS "ActorId",
                       NULL::text AS "ActionKind", NULL::text AS "ActionReason", NULL::text AS "Comment"
                FROM incident_lifecycle_events
                WHERE incident_id = {incidentId}
                UNION ALL
                SELECT event_id AS "EventId", incident_id AS "IncidentId", occurred_at AS "OccurredAt", 1 AS "SourceRank",
                       NULL::text AS "EngineType", NULL::text AS "EngineReason", actor_id AS "ActorId",
                       action_kind AS "ActionKind", reason_code AS "ActionReason", action_note AS "Comment"
                FROM incident_lifecycle_actions
                WHERE incident_id = {incidentId}
                """);
            var ascending = sort == TimelineSort.OccurredAtAsc;
            if (seek is not null)
            {
                var at = seek.OpenedAt;
                var rank = seek.SourceRank!.Value;
                var eventId = seek.IncidentId;
                rows = ascending
                    ? rows.Where(row => row.OccurredAt > at || row.OccurredAt == at &&
                        (row.SourceRank < rank || row.SourceRank == rank && row.EventId.CompareTo(eventId) < 0))
                    : rows.Where(row => row.OccurredAt < at || row.OccurredAt == at &&
                        (row.SourceRank < rank || row.SourceRank == rank && row.EventId.CompareTo(eventId) < 0));
            }
            var ordered = ascending
                ? rows.OrderBy(row => row.OccurredAt).ThenByDescending(row => row.SourceRank).ThenByDescending(row => row.EventId)
                : rows.OrderByDescending(row => row.OccurredAt).ThenByDescending(row => row.SourceRank).ThenByDescending(row => row.EventId);
            var page = await ordered.Take(pageSize + 1).ToArrayAsync(ct);
            var items = page.Take(pageSize).Select(ToLifecycleResponse).ToArray();
            string? next = null;
            if (page.Length > pageSize)
            {
                var last = page[pageSize - 1];
                next = protector.Issue(binding, new IncidentSeek(last.OccurredAt, last.EventId, last.SourceRank));
            }
            var body = CanonicalBytes(new CursorPage<IncidentLifecycleResponse>(items, next, pageSize));
            await transaction.CommitAsync(ct);
            return Results.Bytes(body, Wp07DashboardCanonicalizer.ContentType);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return Results.Empty;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return Unavailable(context, correlation);
        }
    }

    private static async Task<IResult> GetComments(HttpContext context, string id)
    {
        var correlation = (string)context.Items[CorrelationKey]!;
        if (!TryCommentQuery(context.Request.Query, out var sort, out var cursor, out var pageSize))
            return Problem(correlation, "invalid-incident-query", 400);
        if (!CanonicalId(id, out var incidentId)) return Problem(correlation, "invalid-incident-id", 400);

        try
        {
            var protector = context.RequestServices.GetRequiredService<IncidentCursorProtector>();
            var binding = new IncidentCursorBinding("incident-comments", id, null,
                Wp07DashboardContract.SchemaVersion, sort.ToString());
            IncidentSeek? seek = null;
            if (cursor is not null)
            {
                var validation = protector.Validate(cursor, binding, out seek);
                if (validation != IncidentCursorValidation.Valid || seek?.SourceRank is not null)
                    return Problem(correlation, validation == IncidentCursorValidation.FilterMismatch ? "cursor-filter-mismatch" : "invalid-cursor", 400);
            }

            var ct = context.RequestAborted;
            var db = context.RequestServices.GetRequiredService<EePulseDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            if (!await db.AvailabilityIncidents.AsNoTracking().AnyAsync(incident => incident.Id == incidentId, ct))
                return Problem(correlation, "incident-not-found", 404);

            IQueryable<IncidentComment> rows = db.IncidentComments.AsNoTracking().Where(row => row.IncidentId == incidentId);
            var ascending = sort == CommentSort.CreatedAtAsc;
            if (seek is not null)
            {
                var at = seek.OpenedAt;
                var commentId = seek.IncidentId;
                rows = ascending
                    ? rows.Where(row => row.CreatedAt > at || row.CreatedAt == at && row.Id.CompareTo(commentId) < 0)
                    : rows.Where(row => row.CreatedAt < at || row.CreatedAt == at && row.Id.CompareTo(commentId) < 0);
            }
            var ordered = ascending
                ? rows.OrderBy(row => row.CreatedAt).ThenByDescending(row => row.Id)
                : rows.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id);
            var page = await ordered.Take(pageSize + 1).ToArrayAsync(ct);
            var items = page.Take(pageSize).Select(ToCommentResponse).ToArray();
            string? next = null;
            if (page.Length > pageSize)
            {
                var last = page[pageSize - 1];
                next = protector.Issue(binding, new IncidentSeek(last.CreatedAt, last.Id));
            }
            var body = CanonicalBytes(new CursorPage<IncidentCommentResponse>(items, next, pageSize));
            await transaction.CommitAsync(ct);
            return Results.Bytes(body, Wp07DashboardCanonicalizer.ContentType);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return Results.Empty;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return Unavailable(context, correlation);
        }
    }

    private sealed class LifecycleRow
    {
        public Guid EventId { get; init; }
        public Guid IncidentId { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public int SourceRank { get; init; }
        public string? EngineType { get; init; }
        public string? EngineReason { get; init; }
        public Guid? ActorId { get; init; }
        public string? ActionKind { get; init; }
        public string? ActionReason { get; init; }
        public string? Comment { get; init; }
    }

    private static IncidentLifecycleResponse ToLifecycleResponse(LifecycleRow row)
    {
        if (row.EventId == Guid.Empty || row.IncidentId == Guid.Empty || row.OccurredAt.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Incident lifecycle row is invalid.");
        if (row.SourceRank == 0 && row.ActorId is null && row.ActionKind is null && row.ActionReason is null && row.Comment is null &&
            row.EngineType is { } engineType)
        {
            var (type, reason) = engineType switch
            {
                "Opened" => ("opened", "failure-threshold-met"),
                "Resolved" => ("resolved", "recovery-threshold-met"),
                "Occurrence" => ("occurrence", "recovery-failed"),
                _ => throw new InvalidOperationException("Incident lifecycle row is invalid.")
            };
            if (!string.Equals(row.EngineReason, reason, StringComparison.Ordinal))
                throw new InvalidOperationException("Incident lifecycle row is invalid.");
            return new IncidentLifecycleResponse(row.EventId.ToString("D"), row.IncidentId.ToString("D"), type, reason,
                Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(row.OccurredAt), null, null);
        }
        if (row.SourceRank == 1 && row.ActorId is { } actorId && actorId != Guid.Empty && row.EngineType is null && row.EngineReason is null &&
            row.Comment is { Length: > 0 } && row.ActionKind is not null && row.ActionReason is not null)
        {
            var (type, reason) = (row.ActionKind, row.ActionReason) switch
            {
                ("acknowledgement", "operator-acknowledgement") => ("acknowledged", "operator-acknowledgement"),
                ("manual_resolution", "manual-resolution") => ("manually-resolved", "manual-resolution"),
                _ => throw new InvalidOperationException("Incident lifecycle row is invalid.")
            };
            return new IncidentLifecycleResponse(row.EventId.ToString("D"), row.IncidentId.ToString("D"), type, reason,
                Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(row.OccurredAt), actorId.ToString("D"), row.Comment);
        }
        throw new InvalidOperationException("Incident lifecycle row is invalid.");
    }

    private static IncidentCommentResponse ToCommentResponse(IncidentComment row)
    {
        if (row.Id == Guid.Empty || row.IncidentId == Guid.Empty || row.AuthorId == Guid.Empty || string.IsNullOrEmpty(row.Comment) ||
            row.CreatedAt.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Incident comment row is invalid.");
        return new IncidentCommentResponse(row.Id.ToString("D"), row.IncidentId.ToString("D"), row.AuthorId.ToString("D"), row.Comment,
            Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(row.CreatedAt));
    }

    private static bool IsReadFailure(Exception exception) => exception is DbException or DbUpdateException or InvalidOperationException or
        ArgumentException or JsonException or DashboardContractValidationException or OperationCanceledException or System.Security.Cryptography.CryptographicException;

    private static IResult Unavailable(HttpContext context, string correlation)
    {
        var activity = Activity.Current;
        try
        {
            Activity.Current = null;
            using (LogContext.Suspend())
                context.RequestServices.GetRequiredService<Serilog.ILogger>().Error("Incident read request {CorrelationId} failed.", correlation);
        }
        finally { Activity.Current = activity; }
        return Problem(correlation, "incident-read-unavailable", 503);
    }

    private static IncidentResponse ToResponse(ReadRow row)
    {
        var incident = row.Incident;
        if (incident.Id == Guid.Empty || incident.ProbeId == Guid.Empty || incident.RowVersion < 1 || incident.OccurrenceCount < 1 ||
            !Enum.IsDefined(incident.Status) || string.IsNullOrEmpty(row.DeviceName) || string.IsNullOrEmpty(row.SiteName))
            throw new InvalidOperationException("Incident read model is invalid.");
        var status = Enum.Parse<IncidentStatus>(incident.Status.ToString());
        var opened = Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(incident.OpenedAt);
        var resolved = Timestamp(incident.ResolvedAt);
        return new IncidentResponse(incident.Id.ToString("D"), incident.ProbeId.ToString("D"), row.DeviceId.ToString("D"), row.DeviceName,
            row.SiteId.ToString("D"), row.SiteName, incident.RuleKey, status, opened, Timestamp(incident.AcknowledgedAt),
            incident.AcknowledgedBy?.ToString("D"), incident.AcknowledgementComment, resolved, incident.ResolvedBy?.ToString("D"),
            incident.ResolutionNote, incident.OccurrenceCount, IncidentDurationContract.CalculateTotalDowntimeSeconds(status, opened, resolved));
    }

    private static DateTimeOffset? Timestamp(DateTimeOffset? value) => value.HasValue ? Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(value.Value) : null;
    private static bool CanonicalId(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) && value == id.ToString("D");

    internal static bool TryQuery(IQueryCollection query, bool deviceHistory, out IncidentListFilter? filter, out string? cursor, out int pageSize)
    {
        filter = null; cursor = null; pageSize = Wp07DashboardContract.DefaultPageSize;
        string[] allowed = deviceHistory ? ["status", "openedFrom", "openedTo", "sort", "cursor", "pageSize"]
            : ["siteId", "deviceId", "probeId", "status", "openedFrom", "openedTo", "sort", "cursor", "pageSize"];
        if (query.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal) || pair.Value.Count != 1 || string.IsNullOrEmpty(pair.Value[0]))) return false;
        string? Value(string name) => query.TryGetValue(name, out var values) ? values[0] : null;
        foreach (var name in new[] { "siteId", "deviceId", "probeId" }) if (Value(name) is { } value && !CanonicalId(value, out _)) return false;
        IncidentStatus? status = null;
        if (Value("status") is { } statusText)
        {
            if (!Enum.TryParse<IncidentStatus>(statusText, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != statusText) return false;
            status = parsed;
        }
        var sort = IncidentSort.OpenedAtDesc;
        if (Value("sort") is { } sortText && (!Enum.TryParse(sortText, out sort) || !Enum.IsDefined(sort) || sort.ToString() != sortText)) return false;
        if (!TryUtc(Value("openedFrom"), out var from) || !TryUtc(Value("openedTo"), out var to) || from > to) return false;
        if (Value("pageSize") is { } size && (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > 200)) return false;
        cursor = Value("cursor");
        if (cursor?.Length > Wp07DashboardContract.MaximumCursorLength) return false;
        filter = new IncidentListFilter(Value("siteId"), Value("deviceId"), Value("probeId"), status, from, to, sort);
        return true;
    }

    private enum CommentSort { CreatedAtDesc, CreatedAtAsc }

    private static bool TryTimelineQuery(IQueryCollection query, out TimelineSort sort, out string? cursor, out int pageSize)
    {
        sort = TimelineSort.OccurredAtDesc;
        if (!TryReadCursorQuery(query, out var sortText, out cursor, out pageSize)) return false;
        return sortText is null || Enum.TryParse<TimelineSort>(sortText, out sort) && Enum.IsDefined(sort) && sort.ToString() == sortText;
    }

    private static bool TryCommentQuery(IQueryCollection query, out CommentSort sort, out string? cursor, out int pageSize)
    {
        sort = CommentSort.CreatedAtDesc;
        if (!TryReadCursorQuery(query, out var sortText, out cursor, out pageSize)) return false;
        return sortText is null || Enum.TryParse<CommentSort>(sortText, out sort) && Enum.IsDefined(sort) && sort.ToString() == sortText;
    }

    private static bool TryReadCursorQuery(IQueryCollection query, out string? sort, out string? cursor, out int pageSize)
    {
        sort = null;
        cursor = null;
        pageSize = Wp07DashboardContract.DefaultPageSize;
        string[] allowed = ["sort", "cursor", "pageSize"];
        if (query.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal) || pair.Value.Count != 1 || string.IsNullOrEmpty(pair.Value[0]))) return false;
        string? Value(string name) => query.TryGetValue(name, out var values) ? values[0] : null;
        sort = Value("sort");
        if (Value("pageSize") is { } size &&
            (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > 200)) return false;
        cursor = Value("cursor");
        return cursor is null || cursor.Length <= Wp07DashboardContract.MaximumCursorLength;
    }

    private static bool TryUtc(string? value, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (value is null) return true;
        if (value.EndsWith(".Z", StringComparison.Ordinal)) return false;
        if (!DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return false;
        timestamp = parsed;
        return true;
    }

    internal static bool ValidConditional(string[] values)
    {
        if (values.Length == 0) return false;
        if (IncidentConcurrencyContract.ClassifyIfNoneMatch(values, "\"validation\"") == IncidentIfNoneMatchClassification.Invalid) return false;
        // The shared parser tolerates empty list members; the incident contract explicitly rejects them.
        foreach (var value in values)
        {
            var quoted = false;
            var memberStart = 0;
            for (var index = 0; index <= value.Length; index++)
            {
                if (index < value.Length && value[index] == '"') quoted = !quoted;
                if (index == value.Length || value[index] == ',' && !quoted)
                {
                    if (value.AsSpan(memberStart, index - memberStart).Trim().IsEmpty) return false;
                    memberStart = index + 1;
                }
            }
        }
        return true;
    }

    private static byte[] CanonicalBytes<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                if (property.Name is "openedAt" or "acknowledgedAt" or "resolvedAt" or "occurredAt" or "createdAt" && property.Value.ValueKind != JsonValueKind.Null)
                    writer.WriteStringValue(property.Value.GetDateTimeOffset().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
                else WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static IResult Problem(string correlation, string code, int status) => Results.Json(new
    {
        type = "about:blank", title = code, status, code, correlationId = correlation
    }, statusCode: status, contentType: "application/problem+json");
}
