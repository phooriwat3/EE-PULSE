using System.Data;
using System.Data.Common;
using System.Text.Json;
using EePulse.Application.Time;
using EePulse.Contracts.Dashboard;
using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EePulse.Api.Dashboard;

/// <summary>Frozen WP-07 2B1 dashboard-summary read slice.</summary>
public static class DashboardSummaryEndpoints
{
    private const string SafeCorrelationIdItemKey = "WP07.DashboardSummary.SafeCorrelationId";
    private static readonly Action<ILogger, string, Exception?> LogDashboardSummaryUnavailable = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(7001, nameof(LogDashboardSummaryUnavailable)),
        "Dashboard summary request {CorrelationId} failed.");

    public static IEndpointRouteBuilder MapDashboardSummaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Wp07DashboardContract.DashboardSummaryPath, Get)
            .WithName("GetDashboardSummary")
            .WithTags("Dashboard")
            .ExcludeFromDescription()
            .RequireAuthorization(EePulse.Api.Authorization.DashboardAuthorization.DashboardReadPolicy)
            .Produces<DashboardSummaryResponse>()
            .Produces(304)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(Wp07DashboardContract.DashboardSummaryUnavailableStatusCode);
        return endpoints;
    }

    /// <summary>Prevents untrusted request correlation data from reaching this read surface.</summary>
    public static IApplicationBuilder UseDashboardSummaryCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(Wp07DashboardContract.DashboardSummaryPath))
        {
            var correlationId = Guid.NewGuid().ToString("N");
            context.Items[SafeCorrelationIdItemKey] = correlationId;
            context.TraceIdentifier = correlationId;
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            context.Response.OnStarting(() => { context.Response.Headers["X-Correlation-ID"] = correlationId; return Task.CompletedTask; });
        }
        await next(context);
    });

    private static async Task<IResult> Get(HttpContext context, [FromServices] EePulseDbContext db,
        [FromServices] IUtcClock clock, [FromServices] ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(DashboardSummaryEndpoints).FullName ?? "EePulse.Api.Dashboard.DashboardSummaryEndpoints");
        var correlationId = SafeCorrelationId(context);
        if (!TryFilter(context.Request.Query, out var filter))
            return Problem(correlationId, "invalid-dashboard-filter", "The dashboard filter is invalid.");
        if (context.Request.Headers.IfMatch.Count != 0)
            return Problem(correlationId, Wp07DashboardContract.IfMatchUnsupportedCode, "If-Match is not supported for this read.");

        byte[] bytes;
        string etag;
        try
        {
            var response = await ReadAsync(db, clock.UtcNow, filter!, cancellationToken);
            bytes = Wp07DashboardCanonicalizer.SerializeSummary(response);
            etag = Wp07DashboardCanonicalizer.EtagFor(bytes);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or DashboardContractValidationException or InvalidOperationException or ArgumentException)
        {
            LogDashboardSummaryUnavailable(logger, correlationId, null);
            return Problem(correlationId, Wp07DashboardContract.DashboardSummaryUnavailableCode,
                "The dashboard summary is unavailable.", Wp07DashboardContract.DashboardSummaryUnavailableStatusCode);
        }
        var conditional = Wp07DashboardConditionalGet.ClassifyIfNoneMatch(context.Request.Headers.IfNoneMatch, etag);
        if (conditional == DashboardIfNoneMatchClassification.Invalid)
            return Problem(correlationId, Wp07DashboardContract.InvalidIfNoneMatchCode, "The If-None-Match value is invalid.");

        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = Wp07DashboardContract.DashboardCacheControl;
        if (conditional == DashboardIfNoneMatchClassification.Match)
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return new CanonicalJsonResult(bytes);
    }

    private static async Task<DashboardSummaryResponse> ReadAsync(EePulseDbContext db, DateTimeOffset now, DashboardFilter filter,
        CancellationToken cancellationToken)
    {
        // The response combines several independent, bounded reads.  Repeatable read keeps them on
        // one PostgreSQL snapshot, which is also what makes the canonical bytes and strong ETag coherent.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var siteId = filter.SiteId is null ? (Guid?)null : Guid.ParseExact(filter.SiteId, "D");
        var joined =
            from probe in db.Probes.AsNoTracking()
            join device in db.Devices.AsNoTracking() on probe.DeviceId equals device.Id
            join site in db.Sites.AsNoTracking() on device.SiteId equals site.Id
            join projection0 in db.ProbeStatusProjections.AsNoTracking() on probe.Id equals projection0.ProbeId into projections
            from projection in projections.DefaultIfEmpty()
            join incident0 in db.AvailabilityIncidents.AsNoTracking() on projection.OpenIncidentId equals incident0.Id into incidents
            from incident in incidents.DefaultIfEmpty()
            join agent0 in db.Agents.AsNoTracking() on projection.WatermarkAgentId equals agent0.Id into agents
            from agent in agents.DefaultIfEmpty()
            let activeMaintenance = db.MaintenanceWindows.AsNoTracking().Any(window => window.Enabled && window.StartsAt <= now && now < window.EndsAt
                && (window.SiteId == site.Id || window.DeviceId == device.Id || window.ProbeId == probe.Id))
            where (!siteId.HasValue || site.Id == siteId.Value)
                && (filter.Area == null || device.Area == filter.Area)
                && (filter.DeviceType == null || device.DeviceType == filter.DeviceType)
                && (filter.Criticality == null || device.Criticality.ToString() == filter.Criticality)
            select new { probe, device, site, projection, incident, agent, activeMaintenance };
        if (filter.Tag is { } normalizedTag)
        {
            var containedTag = JsonSerializer.SerializeToElement(new[] { normalizedTag });
            joined = joined.Where(row => EF.Functions.JsonContains(EF.Property<List<string>>(row.device, "_tags"), containedTag));
        }

        // Fail closed in PostgreSQL before the frozen effective-status CASE is used by any read.
        if (await joined.AnyAsync(row => row.projection != null
            && row.projection.VisibleStatus != ProbeStatus.Unknown
            && row.projection.VisibleStatus != ProbeStatus.Up
            && row.projection.VisibleStatus != ProbeStatus.Degraded
            && row.projection.VisibleStatus != ProbeStatus.Down
            && row.projection.VisibleStatus != ProbeStatus.Recovering, cancellationToken))
            throw new InvalidOperationException("Dashboard projection status is outside the frozen contract.");

        // Frozen effective-status CASE: disabled > maintenance > persisted status > missing projection.
        var candidates = joined.Select(row => new
        {
            ProbeId = row.probe.Id,
            ProbeEnabled = row.probe.Enabled,
            DeviceId = row.device.Id,
            DeviceEnabled = row.device.Enabled,
            DeviceName = row.device.Name,
            row.device.Area,
            Criticality = row.device.Criticality.ToString(),
            SiteId = row.site.Id,
            SiteName = row.site.Name,
            OpenIncidentId = row.projection == null ? null : row.projection.OpenIncidentId,
            AgentId = row.agent == null ? null : (Guid?)row.agent.Id,
            AgentName = row.agent == null ? null : row.agent.Name,
            AgentGroupId = row.agent == null ? null : (Guid?)row.agent.AgentGroupId,
            AgentStatus = row.agent == null ? null : (AgentStatus?)row.agent.Status,
            AgentSelfHealth = row.agent == null ? null : (AgentSelfHealth?)row.agent.SelfHealth,
            LastHeartbeatAt = row.agent == null ? null : row.agent.LastHeartbeatAt,
            LastReportedAt = row.agent == null ? null : row.agent.LastReportedAt,
            IncidentId = row.incident == null ? null : (Guid?)row.incident.Id,
            IncidentStatus = row.incident == null ? null : (AvailabilityIncidentStatus?)row.incident.Status,
            IncidentOpenedAt = row.incident == null ? null : (DateTimeOffset?)row.incident.OpenedAt,
            IncidentOccurrenceCount = row.incident == null ? null : (int?)row.incident.OccurrenceCount,
            EffectiveStatus = !row.device.Enabled || !row.probe.Enabled ? (int)DashboardProbeStatus.Disabled
                : row.activeMaintenance ? (int)DashboardProbeStatus.Maintenance
                : row.projection == null ? (int)DashboardProbeStatus.Unknown
                : row.projection.VisibleStatus == ProbeStatus.Unknown ? (int)DashboardProbeStatus.Unknown
                : row.projection.VisibleStatus == ProbeStatus.Up ? (int)DashboardProbeStatus.Up
                : row.projection.VisibleStatus == ProbeStatus.Degraded ? (int)DashboardProbeStatus.Degraded
                : row.projection.VisibleStatus == ProbeStatus.Down ? (int)DashboardProbeStatus.Down
                : (int)DashboardProbeStatus.Recovering
        });
        if (filter.Status.HasValue)
            candidates = candidates.Where(row => row.EffectiveStatus == (int)filter.Status.Value);

        var grouped = await candidates
            .Where(row => row.EffectiveStatus >= 0)
            .GroupBy(row => row.EffectiveStatus)
            .Select(group => new StatusTotal(group.Key, group.LongCount()))
            .ToListAsync(cancellationToken);
        var totals = grouped.ToDictionary(row => row.Status, row => row.Count);
        var counts = Enum.GetValues<DashboardProbeStatus>()
            .Select(status => new DashboardStatusCount(status, totals.GetValueOrDefault((int)status)))
            .ToArray();

        var recentRows = await candidates
            .Where(row => row.EffectiveStatus == (int)DashboardProbeStatus.Down
                && row.IncidentId.HasValue
                && (row.IncidentStatus == AvailabilityIncidentStatus.Open || row.IncidentStatus == AvailabilityIncidentStatus.Acknowledged))
            .OrderByDescending(row => row.IncidentOpenedAt)
            .ThenByDescending(row => row.ProbeId)
            .Take(Wp07DashboardContract.DashboardSummaryListMaximum)
            .Select(row => new RecentRow(row.DeviceId, row.DeviceName, row.ProbeId, row.SiteId, row.SiteName, row.Area,
                row.Criticality, row.IncidentOpenedAt!.Value, row.IncidentId!.Value))
            .ToListAsync(cancellationToken);

        var offlineRows = await candidates
            .Where(row => row.AgentId.HasValue && row.AgentStatus == AgentStatus.Offline)
            .GroupBy(row => new
            {
                AgentId = row.AgentId!.Value,
                AgentName = row.AgentName!,
                AgentGroupId = row.AgentGroupId!.Value,
                SelfHealth = row.AgentSelfHealth!.Value,
                row.LastHeartbeatAt,
                row.LastReportedAt
            })
            .Select(group => group.Key)
            .OrderBy(row => row.LastHeartbeatAt.HasValue)
            .ThenBy(row => row.LastHeartbeatAt)
            .ThenBy(row => row.AgentId)
            .Take(Wp07DashboardContract.DashboardSummaryListMaximum)
            .ToListAsync(cancellationToken);

        var openRows = await candidates
            .Where(row => row.IncidentId.HasValue
                && (row.IncidentStatus == AvailabilityIncidentStatus.Open || row.IncidentStatus == AvailabilityIncidentStatus.Acknowledged))
            .OrderByDescending(row => row.IncidentOpenedAt)
            .ThenByDescending(row => row.IncidentId)
            .Take(Wp07DashboardContract.DashboardSummaryListMaximum)
            .Select(row => new OpenRow(row.IncidentId!.Value, row.DeviceId, row.DeviceName, row.ProbeId, row.SiteId, row.SiteName,
                row.IncidentStatus!.Value, row.IncidentOpenedAt!.Value, row.IncidentOccurrenceCount!.Value, row.Criticality))
            .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new DashboardSummaryResponse(Wp07DashboardContract.SchemaVersion, filter, counts,
            recentRows.Select(row => new DashboardRecentDownItem(Id(row.DeviceId), row.DeviceName, Id(row.ProbeId), Id(row.SiteId),
                row.SiteName, row.Area, row.Criticality, Timestamp(row.SinceAt), Id(row.OpenIncidentId))).ToArray(),
            offlineRows.Select(row => new DashboardOfflineAgentItem(Id(row.AgentId), row.AgentName, Id(row.AgentGroupId), "Offline",
                row.SelfHealth.ToString(), OptionalTimestamp(row.LastHeartbeatAt), OptionalTimestamp(row.LastReportedAt))).ToArray(),
            openRows.Select(row => new DashboardOpenIncidentItem(Id(row.IncidentId), Id(row.DeviceId), row.DeviceName, Id(row.ProbeId),
                Id(row.SiteId), row.SiteName, row.Status == AvailabilityIncidentStatus.Open ? IncidentStatus.Open : IncidentStatus.Acknowledged,
                Timestamp(row.OpenedAt), row.OccurrenceCount, row.Criticality)).ToArray());
    }


    private static bool TryFilter(IQueryCollection query, out DashboardFilter? filter)
    {
        filter = null;
        if (!TrySingle(query, "siteId", out var siteId) || !TrySingle(query, "area", out var area) || !TrySingle(query, "deviceType", out var type) || !TrySingle(query, "criticality", out var criticality) || !TrySingle(query, "tag", out var tag) || !TrySingle(query, "status", out var status)) return false;
        DashboardProbeStatus? parsed = null;
        if (status is not null)
        {
            if (!Enum.TryParse(status, false, out DashboardProbeStatus value) || !Enum.IsDefined(value) ||
                !string.Equals(status, value.ToString(), StringComparison.Ordinal)) return false;
            parsed = value;
        }
        try { filter = Wp07DashboardCanonicalizer.NormalizeFilter(new DashboardFilter(siteId, area, type, criticality, tag, parsed)); return true; }
        catch (DashboardContractValidationException) { return false; }
    }

    private static bool TrySingle(IQueryCollection query, string key, out string? value) { value = null; if (!query.TryGetValue(key, out var values)) return true; if (values.Count != 1) return false; value = values[0]; return value is not null; }
    private static string Id(Guid id) => id.ToString("D");
    private static string? Id(Guid? id) => id.HasValue ? Id(id.Value) : null;
    private static DateTimeOffset Timestamp(DateTimeOffset value) => Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(value);
    private static DateTimeOffset? OptionalTimestamp(DateTimeOffset? value) => value.HasValue ? Timestamp(value.Value) : null;
    private static string SafeCorrelationId(HttpContext context) => context.Items.TryGetValue(SafeCorrelationIdItemKey, out var existing) && existing is string id ? id : Guid.NewGuid().ToString("N");
    private static IResult Problem(string correlationId, string code, string detail, int status = StatusCodes.Status400BadRequest) => Results.Problem(detail, statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = correlationId });

    private sealed class CanonicalJsonResult(byte[] bytes) : IResult { public Task ExecuteAsync(HttpContext context) { context.Response.ContentType = Wp07DashboardCanonicalizer.ContentType; context.Response.ContentLength = bytes.Length; return context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask(); } }
    private sealed record StatusTotal(int Status, long Count);
    private sealed record RecentRow(Guid DeviceId, string DeviceName, Guid ProbeId, Guid SiteId, string SiteName, string? Area,
        string Criticality, DateTimeOffset SinceAt, Guid OpenIncidentId);
    private sealed record OfflineRow(Guid AgentId, string AgentName, Guid AgentGroupId, AgentSelfHealth SelfHealth,
        DateTimeOffset? LastHeartbeatAt, DateTimeOffset? LastReportedAt);
    private sealed record OpenRow(Guid IncidentId, Guid DeviceId, string DeviceName, Guid ProbeId, Guid SiteId, string SiteName,
        AvailabilityIncidentStatus Status, DateTimeOffset OpenedAt, int OccurrenceCount, string Criticality);
}
