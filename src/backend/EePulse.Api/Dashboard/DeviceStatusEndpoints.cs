using System.Data;
using System.Data.Common;
using System.Text.Json;
using EePulse.Application.Time;
using EePulse.Contracts.Dashboard;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Serilog.Extensions.Logging;

namespace EePulse.Api.Dashboard;

/// <summary>Frozen WP-07 2B1B device-status read slice.</summary>
public static class DeviceStatusEndpoints
{
    private const string SafeCorrelationIdItemKey = "WP07.DeviceStatus.SafeCorrelationId";
    private static readonly Action<ILogger, string, Exception?> LogDeviceStatusUnavailable = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(7002, nameof(LogDeviceStatusUnavailable)),
        "Device status request {CorrelationId} failed.");

    public static IEndpointRouteBuilder MapDeviceStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Wp07DashboardContract.DeviceStatusPathTemplate, Get)
            .WithName("GetDeviceStatus")
            .WithTags("Dashboard")
            .ExcludeFromDescription()
            .RequireAuthorization(EePulse.Api.Authorization.DashboardAuthorization.DashboardReadPolicy)
            .Produces<StatusEnrichedDeviceResponse>()
            .Produces(304).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(Wp07DashboardContract.DeviceStatusUnavailableStatusCode);
        return endpoints;
    }

    public static IApplicationBuilder UseDeviceStatusCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (IsDeviceStatusPath(context.Request.Path))
        {
            var originalTraceIdentifier = context.TraceIdentifier;
            var correlation = Guid.NewGuid().ToString("N");
            context.Items[SafeCorrelationIdItemKey] = correlation;
            context.TraceIdentifier = correlation;
            context.Response.Headers["X-Correlation-ID"] = correlation;
            context.Response.OnStarting(() => { context.Response.Headers["X-Correlation-ID"] = correlation; return Task.CompletedTask; });
            try
            {
                using (LogContext.PushProperty("CorrelationId", correlation))
                {
                    await next(context);
                }
            }
            finally
            {
                context.TraceIdentifier = originalTraceIdentifier;
            }

            return;
        }

        await next(context);
    });

    private static bool IsDeviceStatusPath(PathString path)
    {
        const string prefix = "/api/v1/devices/";
        const string statusSegment = "status";
        var value = path.Value;
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var idStart = prefix.Length;
        var idEnd = value.IndexOf('/', idStart);
        if (idEnd < idStart) return false;

        var statusStart = idEnd + 1;
        while (statusStart < value.Length && value[statusStart] == '/')
        {
            statusStart++;
        }

        var statusEnd = statusStart + statusSegment.Length;
        if (value.Length < statusEnd || !value.AsSpan(statusStart, statusSegment.Length).Equals(statusSegment.AsSpan(), StringComparison.OrdinalIgnoreCase)) return false;
        return value.Length == statusEnd || value[statusEnd] == '/';
    }

    private static async Task<IResult> Get(string id, HttpContext context, [FromServices] EePulseDbContext db,
        [FromServices] IUtcClock clock, [FromServices] Serilog.ILogger serilogLogger, CancellationToken cancellationToken)
    {
        var correlation = SafeCorrelationId(context);
        if (!TryParseCanonicalId(id, out var deviceId)) return Problem(correlation, Wp07DashboardContract.InvalidDeviceStatusIdCode, "The device status id is invalid.");
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (context.Request.Query.Count != 0 || context.Request.QueryString.HasValue || rawTarget?.Contains('?') == true)
            return Problem(correlation, Wp07DashboardContract.InvalidDeviceStatusQueryCode, "The device status query is invalid.");
        if (context.Request.Headers.IfMatch.Count != 0) return Problem(correlation, Wp07DashboardContract.IfMatchUnsupportedCode, "If-Match is not supported for this read.");

        try
        {
            var response = await ReadAsync(db, deviceId, clock.UtcNow, cancellationToken);
            if (response is null) return Results.NotFound();
            var bytes = Wp07DashboardCanonicalizer.SerializeDeviceStatus(response);
            var etag = Wp07DashboardCanonicalizer.EtagFor(bytes);
            var conditional = Wp07DashboardConditionalGet.ClassifyIfNoneMatch(context.Request.Headers.IfNoneMatch, etag);
            if (conditional == DashboardIfNoneMatchClassification.Invalid)
                return Problem(correlation, Wp07DashboardContract.InvalidIfNoneMatchCode, "The If-None-Match value is invalid.");
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = Wp07DashboardContract.DashboardCacheControl;
            return conditional == DashboardIfNoneMatchClassification.Match ? Results.StatusCode(StatusCodes.Status304NotModified) : new CanonicalJsonResult(bytes);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return AbortResult.Instance;
        }
        catch (OperationCanceledException)
        {
            return Unavailable(serilogLogger, correlation);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or DashboardContractValidationException or InvalidOperationException or ArgumentException or JsonException)
        {
            return Unavailable(serilogLogger, correlation);
        }
    }

    private static SafeProblemResult Unavailable(Serilog.ILogger serilogLogger, string correlation)
    {
        using var provider = new SerilogLoggerProvider(serilogLogger, dispose: false);
        var logger = provider.CreateLogger(typeof(DeviceStatusEndpoints).FullName ?? "EePulse.Api.Dashboard.DeviceStatusEndpoints");
        using (LogContext.Suspend())
        {
            LogDeviceStatusUnavailable(logger, correlation, null);
        }

        return Problem(correlation, Wp07DashboardContract.DeviceStatusUnavailableCode, "The device status is unavailable.", Wp07DashboardContract.DeviceStatusUnavailableStatusCode);
    }

    private static async Task<StatusEnrichedDeviceResponse?> ReadAsync(EePulseDbContext db, Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var device = await db.Devices.AsNoTracking().Where(value => value.Id == deviceId).Select(value => new
        { value.Id, value.SiteId, value.Name, value.Address, value.Hostname, value.DeviceType, value.Area, value.Owner, value.Criticality, value.Enabled, value.RowVersion, Tags = EF.Property<List<string>>(value, "_tags") }).SingleOrDefaultAsync(ct);
        if (device is null) { await transaction.CommitAsync(ct); return null; }
        var rows = await (
            from probe in db.Probes.AsNoTracking()
            join projection0 in db.ProbeStatusProjections.AsNoTracking() on probe.Id equals projection0.ProbeId into projections
            from projection in projections.DefaultIfEmpty()
            join agent0 in db.Agents.AsNoTracking() on projection.WatermarkAgentId equals agent0.Id into agents
            from agent in agents.DefaultIfEmpty()
            join ledger0 in db.ProbeResultLedgerEntries.AsNoTracking() on new
            {
                AgentId = projection.WatermarkAgentId,
                ResultId = projection.WatermarkResultId,
                ProbeId = (Guid?)probe.Id
            }
                equals new
                {
                    AgentId = (Guid?)ledger0.AgentId,
                    ResultId = (Guid?)ledger0.ResultId,
                    ProbeId = (Guid?)ledger0.ProbeId
                } into ledgers
            from ledger in ledgers.DefaultIfEmpty()
            let maintenance = db.MaintenanceWindows.AsNoTracking().Any(window => window.Enabled && window.StartsAt <= now && now < window.EndsAt &&
                (window.SiteId == device.SiteId || window.DeviceId == device.Id || window.ProbeId == probe.Id))
            where probe.DeviceId == deviceId
            select new { probe.Id, probe.Enabled, Projection = projection, AgentId = agent == null ? null : (Guid?)agent.Id, AgentName = agent == null ? null : agent.Name,
                LedgerReceivedAt = ledger == null ? null : (DateTimeOffset?)ledger.ReceivedAt,
                LedgerEndedAt = ledger == null ? null : (DateTimeOffset?)ledger.EndedAt, maintenance }).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        var probes = rows.OrderBy(value => value.Id.ToString("D"), StringComparer.Ordinal).Select(row =>
        {
            if (row.Projection is null)
            {
                var missingProjectionVisible = Wp07DashboardCanonicalizer.ResolveVisibleStatus(device.Enabled, row.Enabled, row.maintenance, null);
                return new DeviceStatusResponse(Id(row.Id), missingProjectionVisible, DashboardProbeStatus.Unknown, 0, null, null, null, null, null);
            }

            var projection = row.Projection;
            var watermarkCount = new[]
            {
                projection.WatermarkEventAt.HasValue,
                projection.WatermarkAgentId.HasValue,
                projection.WatermarkResultId.HasValue
            }.Count(value => value);

            if (watermarkCount is not 0 and not 3 ||
                watermarkCount == 3 && (!row.AgentId.HasValue || !row.LedgerReceivedAt.HasValue ||
                    row.LedgerEndedAt != projection.WatermarkEventAt))
            {
                throw new InvalidOperationException("Invalid watermark read model.");
            }

            var underlying = Map(projection.UnderlyingStatus);
            var visible = Wp07DashboardCanonicalizer.ResolveVisibleStatus(device.Enabled, row.Enabled, row.maintenance, Map(projection.VisibleStatus));

            return new DeviceStatusResponse(
                Id(row.Id),
                visible,
                underlying,
                projection.StateVersion,
                Timestamp(projection.LastFreshEventAt),
                Timestamp(row.LedgerReceivedAt),
                Id(row.AgentId),
                row.AgentName,
                Id(projection.OpenIncidentId));
            }).ToArray();
        return new StatusEnrichedDeviceResponse(Id(device.Id), Id(device.SiteId), device.Name, device.Address, device.Hostname, device.DeviceType, device.Area, device.Owner,
            device.Criticality.ToString(), device.Tags.ToArray(), device.Enabled, device.RowVersion, probes);
    }

    private static DashboardProbeStatus Map(ProbeStatus status) => status switch { ProbeStatus.Unknown => DashboardProbeStatus.Unknown, ProbeStatus.Up => DashboardProbeStatus.Up, ProbeStatus.Degraded => DashboardProbeStatus.Degraded, ProbeStatus.Down => DashboardProbeStatus.Down, ProbeStatus.Recovering => DashboardProbeStatus.Recovering, _ => throw new InvalidOperationException("Invalid persisted status.") };
    private static bool TryParseCanonicalId(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);
    private static string Id(Guid value) => value.ToString("D"); private static string? Id(Guid? value) => value.HasValue ? Id(value.Value) : null;
    private static DateTimeOffset? Timestamp(DateTimeOffset? value) => value.HasValue ? Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(value.Value) : null;
    private static string SafeCorrelationId(HttpContext context) => context.Items.TryGetValue(SafeCorrelationIdItemKey, out var item) && item is string id ? id : Guid.NewGuid().ToString("N");
    // Device-status failures must not pass through the global Problem Details writer: its traceId may inherit caller traceparent.
    private static SafeProblemResult Problem(string correlation, string code, string detail, int status = 400) => new SafeProblemResult(correlation, code, detail, status);
    private sealed class CanonicalJsonResult(byte[] bytes) : IResult { public Task ExecuteAsync(HttpContext context) { context.Response.ContentType = Wp07DashboardCanonicalizer.ContentType; context.Response.ContentLength = bytes.Length; return context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask(); } }
    private sealed class AbortResult : IResult
    {
        public static AbortResult Instance { get; } = new();
        public Task ExecuteAsync(HttpContext context) { context.Abort(); return Task.CompletedTask; }
    }
    private sealed class SafeProblemResult(string correlation, string code, string detail, int status) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            return JsonSerializer.SerializeAsync(context.Response.Body, new
            {
                type = status == StatusCodes.Status503ServiceUnavailable
                    ? "https://tools.ietf.org/html/rfc9110#section-15.6.4"
                    : "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                title = code,
                status,
                detail,
                code,
                correlationId = correlation
            }, cancellationToken: context.RequestAborted);
        }
    }
}
