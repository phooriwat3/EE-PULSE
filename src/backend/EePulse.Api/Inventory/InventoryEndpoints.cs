using System.Security.Claims;
using System.Text.Json;
using System.Text;
using EePulse.Api.Authorization;
using EePulse.Api.Agents;
using EePulse.Application.Time;
using EePulse.Contracts.Inventory;
using EePulse.Domain.Auditing;
using EePulse.Domain.Common;
using EePulse.Domain.Inventory;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EePulse.Api.Inventory;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sites = endpoints.MapGroup("/api/v1/sites").WithTags("Inventory - Sites");
        sites.MapGet("/", ListSites).Produces<PagedResponse<SiteResponse>>().ProducesProblem(400)
            .RequireAuthorization(InventoryAuthorization.ReadPolicy);
        sites.MapPost("/", CreateSite).Produces<SiteResponse>(201).ProducesProblem(400).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.AdminPolicy);
        sites.MapPut("/{id:guid}", UpdateSite).Produces<SiteResponse>().Produces(404).ProducesProblem(400).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.AdminPolicy);

        var groups = endpoints.MapGroup("/api/v1/agent-groups").WithTags("Inventory - Agent Groups");
        groups.MapGet("/", ListAgentGroups).Produces<PagedResponse<AgentGroupResponse>>().ProducesProblem(400)
            .RequireAuthorization(InventoryAuthorization.ReadPolicy);
        groups.MapPost("/", CreateAgentGroup).Produces<AgentGroupResponse>(201).ProducesProblem(400).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.AdminPolicy);
        groups.MapPut("/{id:guid}", UpdateAgentGroup).Produces<AgentGroupResponse>().Produces(404).ProducesProblem(400).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.AdminPolicy);

        var devices = endpoints.MapGroup("/api/v1/devices").WithTags("Inventory - Devices");
        devices.MapGet("/", ListDevices).Produces<PagedResponse<DeviceResponse>>().ProducesProblem(400)
            .RequireAuthorization(InventoryAuthorization.ReadPolicy);
        devices.MapGet("/{id:guid}", GetDevice).Produces<DeviceResponse>().Produces(404)
            .RequireAuthorization(InventoryAuthorization.ReadPolicy);
        devices.MapPost("/", CreateDevice).Produces<DeviceResponse>(201).Produces(404).ProducesProblem(400).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.WritePolicy);
        devices.MapPut("/{id:guid}", UpdateDevice).Produces<DeviceResponse>().Produces(404).ProducesProblem(400).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.WritePolicy);
        devices.MapDelete("/{id:guid}", DeleteDevice).Produces(204).Produces(404).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.AdminPolicy);
        devices.MapPost("/import/preview", PreviewImport).Accepts<string>("text/csv").Produces<CsvImportPreviewResponse>()
            .ProducesProblem(400).ProducesProblem(413).ProducesProblem(429)
            .RequireAuthorization(InventoryAuthorization.WritePolicy);
        devices.MapPost("/import/commit", CommitImport).Produces<CsvImportCommitResponse>().ProducesProblem(400)
            .ProducesProblem(403).ProducesProblem(409).RequireAuthorization(InventoryAuthorization.WritePolicy);

        var probes = endpoints.MapGroup("/api/v1/probes").WithTags("Inventory - Probes");
        probes.MapGet("/", ListProbes).Produces<PagedResponse<ProbeResponse>>().ProducesProblem(400)
            .RequireAuthorization(InventoryAuthorization.ReadPolicy);
        probes.MapPost("/", CreateProbe).Produces<ProbeResponse>(201).Produces(404).ProducesProblem(400).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.WritePolicy);
        probes.MapPut("/{id:guid}", UpdateProbe).Produces<ProbeResponse>().Produces(404).ProducesProblem(400).ProducesProblem(409)
            .RequireAuthorization(InventoryAuthorization.WritePolicy);

        var maintenance = endpoints.MapGroup("/api/v1/maintenance-windows").WithTags("Inventory - Maintenance");
        maintenance.MapGet("/", ListMaintenance).Produces<PagedResponse<MaintenanceWindowResponse>>().ProducesProblem(400)
            .RequireAuthorization(InventoryAuthorization.ReadPolicy);
        maintenance.MapPost("/", CreateMaintenance).Produces<MaintenanceWindowResponse>(201).Produces(404)
            .ProducesProblem(400).ProducesProblem(409).RequireAuthorization(InventoryAuthorization.WritePolicy);
        maintenance.MapPut("/{id:guid}", UpdateMaintenance).Produces<MaintenanceWindowResponse>().Produces(404)
            .ProducesProblem(400).ProducesProblem(409).RequireAuthorization(InventoryAuthorization.WritePolicy);

        return endpoints;
    }

    private static Task<IResult> PreviewImport(
        HttpRequest request, DeviceCsvImportService imports, EePulseDbContext db, IUtcClock clock,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        if (request.ContentLength is > DeviceCsvImportService.MaximumBytes)
        {
            return Results.Problem("CSV content is too large.", statusCode: 413);
        }

        var csv = await ReadLimitedUtf8Async(request.Body, DeviceCsvImportService.MaximumBytes, cancellationToken);
        var actorText = request.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(actorText, out var actorId) || actorId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await imports.PreviewAsync(csv, actorId, db, clock, cancellationToken));
    });

    private static Task<IResult> CommitImport(
        CsvImportCommitRequest request, DeviceCsvImportService imports, EePulseDbContext db, IUtcClock clock,
        HttpContext http, CancellationToken cancellationToken) => Mutate(async () =>
            Results.Ok(await imports.CommitAsync(request.PreviewToken, db, clock, http, cancellationToken)));

    private static async Task<IResult> ListSites(
        EePulseDbContext db, int page = 1, int pageSize = 50, bool? enabled = null, string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidPage(page, pageSize, out var problem)) return problem;
        var query = db.Sites.AsNoTracking();
        if (enabled.HasValue) query = query.Where(site => site.Enabled == enabled.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(site => EF.Functions.ILike(site.Code, $"%{term}%") || EF.Functions.ILike(site.Name, $"%{term}%"));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderBy(site => site.Code).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(site => ToResponse(site)).ToListAsync(cancellationToken);
        return Results.Ok(new PagedResponse<SiteResponse>(items, page, pageSize, total));
    }

    private static Task<IResult> CreateSite(
        CreateSiteRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var site = new Site(Guid.NewGuid(), request.Code, request.Name, request.Timezone, clock.UtcNow);
        db.Sites.Add(site);
        AddAudit(db, http, clock, "inventory.site.created", "Site", site.Id, null, request);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/sites/{site.Id}", ToResponse(site));
    });

    private static Task<IResult> UpdateSite(
        Guid id, UpdateSiteRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var site = await db.Sites.FindAsync([id], cancellationToken);
        if (site is null) return Results.NotFound();
        var before = ToResponse(site);
        db.Entry(site).Property(candidate => candidate.RowVersion).OriginalValue = request.RowVersion;
        site.Update(request.Code, request.Name, request.Timezone, request.Enabled, clock.UtcNow);
        AddAudit(db, http, clock, "inventory.site.updated", "Site", id, before, request);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(site));
    });

    private static async Task<IResult> ListAgentGroups(
        EePulseDbContext db, int page = 1, int pageSize = 50, bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidPage(page, pageSize, out var problem)) return problem;
        var query = db.AgentGroups.AsNoTracking();
        if (enabled.HasValue) query = query.Where(group => group.Enabled == enabled.Value);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderBy(group => group.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(group => ToResponse(group)).ToListAsync(cancellationToken);
        return Results.Ok(new PagedResponse<AgentGroupResponse>(items, page, pageSize, total));
    }

    private static Task<IResult> CreateAgentGroup(
        CreateAgentGroupRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var group = new AgentGroup(Guid.NewGuid(), request.Name, request.Description, clock.UtcNow);
        db.AgentGroups.Add(group);
        AddAudit(db, http, clock, "inventory.agent-group.created", "AgentGroup", group.Id, null, request);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/agent-groups/{group.Id}", ToResponse(group));
    });

    private static Task<IResult> UpdateAgentGroup(
        Guid id, UpdateAgentGroupRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var group = await db.AgentGroups.FindAsync([id], cancellationToken);
        if (group is null) return Results.NotFound();
        var before = ToResponse(group);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Entry(group).Property(candidate => candidate.RowVersion).OriginalValue = request.RowVersion;
        group.Update(request.Name, request.Description, request.Enabled, clock.UtcNow);
        AddAudit(db, http, clock, "inventory.agent-group.updated", "AgentGroup", id, before, request);
        await db.SaveChangesAsync(cancellationToken);
        if (before.Enabled != request.Enabled) await PublishConfiguredGroups(db, [group.Id], clock.UtcNow, http, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToResponse(group));
    });

    private static async Task<IResult> ListDevices(
        EePulseDbContext db, int page = 1, int pageSize = 50, Guid? siteId = null, string? area = null,
        string? deviceType = null, Criticality? criticality = null, string? tag = null, bool? enabled = null,
        string? search = null, CancellationToken cancellationToken = default)
    {
        if (!ValidPage(page, pageSize, out var problem)) return problem;
        var query = db.Devices.AsNoTracking();
        if (siteId.HasValue) query = query.Where(device => device.SiteId == siteId.Value);
        if (!string.IsNullOrWhiteSpace(area)) query = query.Where(device => device.Area == area.Trim());
        if (!string.IsNullOrWhiteSpace(deviceType)) query = query.Where(device => device.DeviceType == deviceType.Trim());
        if (criticality.HasValue) query = query.Where(device => device.Criticality == criticality.Value);
        if (enabled.HasValue) query = query.Where(device => device.Enabled == enabled.Value);
        if (!string.IsNullOrWhiteSpace(tag)) query = query.Where(device => EF.Functions.JsonContains(EF.Property<List<string>>(device, "_tags"), JsonSerializer.Serialize(new[] { tag.Trim().ToLowerInvariant() })));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (System.Net.IPAddress.TryParse(term, out var parsedAddress) &&
                parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var canonicalAddress = parsedAddress.ToString();
                query = query.Where(device => device.Address == canonicalAddress);
            }
            else
            {
                query = query.Where(device => EF.Functions.ILike(device.Name, $"%{term}%") ||
                    (device.Hostname != null && EF.Functions.ILike(device.Hostname, $"%{term}%")));
            }
        }

        var total = await query.LongCountAsync(cancellationToken);
        var devices = await query.OrderBy(device => device.Name).ThenBy(device => device.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return Results.Ok(new PagedResponse<DeviceResponse>(devices.Select(ToResponse).ToArray(), page, pageSize, total));
    }

    private static async Task<IResult> GetDevice(Guid id, EePulseDbContext db, CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return device is null ? Results.NotFound() : Results.Ok(ToResponse(device));
    }

    private static Task<IResult> CreateDevice(
        CreateDeviceRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var siteId = RequiredGuid(request.SiteId, nameof(request.SiteId));
        if (!await db.Sites.AnyAsync(site => site.Id == siteId, cancellationToken)) return Results.NotFound();
        var device = new Device(Guid.NewGuid(), siteId, request.Name, request.Address, request.Hostname,
            request.DeviceType, request.Area, request.Owner, RequiredCriticality(request.Criticality), request.Tags, clock.UtcNow);
        db.Devices.Add(device);
        AddAudit(db, http, clock, "inventory.device.created", "Device", device.Id, null, request);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/devices/{device.Id}", ToResponse(device));
    });

    private static Task<IResult> UpdateDevice(
        Guid id, UpdateDeviceRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var device = await db.Devices.FindAsync([id], cancellationToken);
        if (device is null) return Results.NotFound();
        var siteId = RequiredGuid(request.SiteId, nameof(request.SiteId));
        if (!await db.Sites.AnyAsync(site => site.Id == siteId, cancellationToken)) return Results.NotFound();
        var before = ToResponse(device);
        var affectedGroups = await db.Probes.Where(probe => probe.DeviceId == id).Select(probe => probe.AgentGroupId).Distinct().ToListAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Entry(device).Property(candidate => candidate.RowVersion).OriginalValue = request.RowVersion;
        device.Update(siteId, request.Name, request.Address, request.Hostname, request.DeviceType, request.Area,
            request.Owner, RequiredCriticality(request.Criticality), request.Tags, request.Enabled, clock.UtcNow);
        AddAudit(db, http, clock, "inventory.device.updated", "Device", id, before, request);
        await db.SaveChangesAsync(cancellationToken);
        if (before.Address != request.Address || before.Enabled != request.Enabled) { await PublishConfiguredGroups(db, affectedGroups, clock.UtcNow, http, cancellationToken); await db.SaveChangesAsync(cancellationToken); }
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToResponse(device));
    });

    private static Task<IResult> DeleteDevice(
        Guid id, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var device = await db.Devices.FindAsync([id], cancellationToken);
        if (device is null) return Results.NotFound();
        var before = ToResponse(device);
        db.Devices.Remove(device);
        AddAudit(db, http, clock, "inventory.device.deleted", "Device", id, before, null);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    });

    private static async Task<IResult> ListProbes(
        EePulseDbContext db, int page = 1, int pageSize = 50, Guid? deviceId = null,
        Guid? agentGroupId = null, bool? enabled = null, CancellationToken cancellationToken = default)
    {
        if (!ValidPage(page, pageSize, out var problem)) return problem;
        var query = db.Probes.AsNoTracking();
        if (deviceId.HasValue) query = query.Where(probe => probe.DeviceId == deviceId.Value);
        if (agentGroupId.HasValue) query = query.Where(probe => probe.AgentGroupId == agentGroupId.Value);
        if (enabled.HasValue) query = query.Where(probe => probe.Enabled == enabled.Value);
        var total = await query.LongCountAsync(cancellationToken);
        var probes = await query.OrderBy(probe => probe.DeviceId).ThenBy(probe => probe.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return Results.Ok(new PagedResponse<ProbeResponse>(probes.Select(ToResponse).ToArray(), page, pageSize, total));
    }

    private static Task<IResult> CreateProbe(
        CreateProbeRequest request, EePulseDbContext db, HttpContext http, IUtcClock clock,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var deviceId = RequiredGuid(request.DeviceId, nameof(request.DeviceId));
        var groupId = RequiredGuid(request.AgentGroupId, nameof(request.AgentGroupId));
        if (!await db.Devices.AnyAsync(device => device.Id == deviceId, cancellationToken) ||
            !await db.AgentGroups.AnyAsync(group => group.Id == groupId, cancellationToken)) return Results.NotFound();
        var probe = new Probe(Guid.NewGuid(), deviceId, groupId, request.IntervalSeconds, request.TimeoutMilliseconds,
            request.AttemptCount, request.WarningRttMilliseconds, request.CriticalRttMilliseconds,
            request.FailureThreshold, request.RecoveryThreshold);
        db.Probes.Add(probe);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        AddAudit(db, http, clock, "inventory.probe.created", "Probe", probe.Id, null, request);
        await db.SaveChangesAsync(cancellationToken);
        await PublishConfiguredGroups(db, [groupId], clock.UtcNow, http, cancellationToken); await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/api/v1/probes/{probe.Id}", ToResponse(probe));
    });

    private static Task<IResult> UpdateProbe(
        Guid id, UpdateProbeRequest request, EePulseDbContext db, HttpContext http, IUtcClock clock,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var probe = await db.Probes.FindAsync([id], cancellationToken);
        if (probe is null) return Results.NotFound();
        var groupId = RequiredGuid(request.AgentGroupId, nameof(request.AgentGroupId));
        if (!await db.AgentGroups.AnyAsync(group => group.Id == groupId, cancellationToken)) return Results.NotFound();
        var before = ToResponse(probe);
        var oldGroupId = probe.AgentGroupId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Entry(probe).Property(candidate => candidate.RowVersion).OriginalValue = request.RowVersion;
        probe.Update(groupId, request.IntervalSeconds, request.TimeoutMilliseconds, request.AttemptCount,
            request.WarningRttMilliseconds, request.CriticalRttMilliseconds, request.FailureThreshold,
            request.RecoveryThreshold, request.Enabled);
        AddAudit(db, http, clock, "inventory.probe.updated", "Probe", id, before, request);
        await db.SaveChangesAsync(cancellationToken);
        await PublishConfiguredGroups(db, [oldGroupId, groupId], clock.UtcNow, http, cancellationToken); await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToResponse(probe));
    });

    private static async Task<IResult> ListMaintenance(
        EePulseDbContext db, int page = 1, int pageSize = 50, bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidPage(page, pageSize, out var problem)) return problem;
        var query = db.MaintenanceWindows.AsNoTracking();
        if (enabled.HasValue) query = query.Where(window => window.Enabled == enabled.Value);
        var total = await query.LongCountAsync(cancellationToken);
        var windows = await query.OrderByDescending(window => window.StartsAt).ThenBy(window => window.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return Results.Ok(new PagedResponse<MaintenanceWindowResponse>(windows.Select(ToResponse).ToArray(), page, pageSize, total));
    }

    private static Task<IResult> CreateMaintenance(
        CreateMaintenanceWindowRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var window = new MaintenanceWindow(Guid.NewGuid(), request.Name, request.StartsAt, request.EndsAt,
            request.Timezone, OptionalGuid(request.SiteId, nameof(request.SiteId)),
            OptionalGuid(request.DeviceId, nameof(request.DeviceId)), OptionalGuid(request.ProbeId, nameof(request.ProbeId)), clock.UtcNow);
        if (!await ScopeExists(window, db, cancellationToken)) return Results.NotFound();
        db.MaintenanceWindows.Add(window);
        AddAudit(db, http, clock, "inventory.maintenance.created", "MaintenanceWindow", window.Id, null, request);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/maintenance-windows/{window.Id}", ToResponse(window));
    });

    private static Task<IResult> UpdateMaintenance(
        Guid id, UpdateMaintenanceWindowRequest request, EePulseDbContext db, IUtcClock clock, HttpContext http,
        CancellationToken cancellationToken) => Mutate(async () =>
    {
        var window = await db.MaintenanceWindows.FindAsync([id], cancellationToken);
        if (window is null) return Results.NotFound();
        var before = ToResponse(window);
        db.Entry(window).Property(candidate => candidate.RowVersion).OriginalValue = request.RowVersion;
        window.Update(request.Name, request.StartsAt, request.EndsAt, request.Timezone,
            OptionalGuid(request.SiteId, nameof(request.SiteId)), OptionalGuid(request.DeviceId, nameof(request.DeviceId)),
            OptionalGuid(request.ProbeId, nameof(request.ProbeId)), request.Enabled, clock.UtcNow);
        if (!await ScopeExists(window, db, cancellationToken)) return Results.NotFound();
        AddAudit(db, http, clock, "inventory.maintenance.updated", "MaintenanceWindow", id, before, request);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(window));
    });

    private static async Task<bool> ScopeExists(MaintenanceWindow window, EePulseDbContext db, CancellationToken token) =>
        window.SiteId.HasValue ? await db.Sites.AnyAsync(site => site.Id == window.SiteId, token) :
        window.DeviceId.HasValue ? await db.Devices.AnyAsync(device => device.Id == window.DeviceId, token) :
        await db.Probes.AnyAsync(probe => probe.Id == window.ProbeId, token);

    private static bool ValidPage(int page, int pageSize, out IResult problem)
    {
        if (page < 1 || pageSize is < 1 or > 200)
        {
            problem = Results.Problem("page must be at least 1 and pageSize must be between 1 and 200.", statusCode: 400);
            return false;
        }

        problem = Results.Empty;
        return true;
    }

    private static Guid RequiredGuid(string value, string field) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new DomainValidationException(field, $"{field} must be a non-empty UUID.");

    private static Guid? OptionalGuid(string? value, string field) => string.IsNullOrWhiteSpace(value) ? null : RequiredGuid(value, field);

    private static Criticality RequiredCriticality(string value) =>
        Enum.TryParse<Criticality>(value, true, out var criticality) && Enum.IsDefined(criticality)
            ? criticality
            : throw new DomainValidationException(nameof(value), "Criticality must be Low, Normal, High, or Critical.");

    private static async Task<IResult> Mutate(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (AgentEndpoints.AgentConfigurationPublicationException exception)
        {
            return Results.Json(new { type = $"https://ee-pulse.invalid/problems/{EePulse.Contracts.Agents.AgentProblemCodes.ConfigurationConflict}", title = EePulse.Contracts.Agents.AgentProblemCodes.ConfigurationConflict, status = 409, detail = "The mutation would produce an invalid Agent configuration.", instance = exception.Instance, code = EePulse.Contracts.Agents.AgentProblemCodes.ConfigurationConflict, retryable = false, correlationId = exception.CorrelationId }, statusCode: 409, contentType: "application/problem+json");
        }
        catch (DomainValidationException exception)
        {
            return Results.Problem(exception.Message, statusCode: 400, title: "Validation failed",
                extensions: new Dictionary<string, object?> { ["field"] = exception.Field });
        }
        catch (InvalidDataException exception)
        {
            return Results.Problem(exception.Message, statusCode: 413, title: "Payload too large");
        }
        catch (DecoderFallbackException exception)
        {
            return Results.Problem(exception.Message, statusCode: 400, title: "Invalid UTF-8");
        }
        catch (PreviewCapacityException exception)
        {
            return Results.Problem(exception.Message, statusCode: 429, title: "Preview capacity reached");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem("The resource changed since it was read. Reload and retry.", statusCode: 409,
                title: "Concurrency conflict");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Results.Problem(exception.Message, statusCode: 403, title: "Forbidden");
        }
        catch (DbUpdateException)
        {
            return Results.Problem("The change conflicts with an existing resource or dependency.", statusCode: 409,
                title: "Persistence conflict");
        }
    }

    private static async Task<string> ReadLimitedUtf8Async(Stream body, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"CSV content must not exceed {maximumBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static void AddAudit(
        EePulseDbContext db, HttpContext http, IUtcClock clock, string action, string entityType,
        Guid entityId, object? before, object? after)
    {
        var actorText = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? actorId = Guid.TryParse(actorText, out var parsed) && parsed != Guid.Empty ? parsed : null;
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), actorId, action, entityType, entityId,
            before is null ? null : JsonSerializer.Serialize(before),
            after is null ? null : JsonSerializer.Serialize(after),
            http.TraceIdentifier, clock.UtcNow, http.Connection.RemoteIpAddress?.ToString()));
    }

    private static SiteResponse ToResponse(Site site) => new(site.Id.ToString(), site.Code, site.Name, site.Timezone,
        site.Enabled, site.CreatedAt, site.UpdatedAt, site.RowVersion);
    private static async Task PublishConfiguredGroups(EePulseDbContext db, IEnumerable<Guid> groupIds, DateTimeOffset now, HttpContext http, CancellationToken ct)
    {
        foreach (var groupId in groupIds.Distinct().OrderBy(x => x))
        {
            var group = await db.AgentGroups.FromSqlInterpolated($"SELECT * FROM agent_groups WHERE id = {groupId} FOR UPDATE").SingleAsync(ct);
            if (!await db.AgentGroupAllowedNetworks.AnyAsync(x => x.AgentGroupId == groupId, ct)) continue;
            await AgentEndpoints.Publish(db, group, now, null, ct, null, http);
        }
    }

    private static AgentGroupResponse ToResponse(AgentGroup group) => new(group.Id.ToString(), group.Name, group.Description,
        group.Enabled, group.CreatedAt, group.UpdatedAt, group.RowVersion);
    private static DeviceResponse ToResponse(Device device) => new(device.Id.ToString(), device.SiteId.ToString(), device.Name,
        device.Address, device.Hostname, device.DeviceType, device.Area, device.Owner, device.Criticality.ToString(),
        device.Tags.ToArray(), device.Enabled, device.CreatedAt, device.UpdatedAt, device.RowVersion);
    private static ProbeResponse ToResponse(Probe probe) => new(probe.Id.ToString(), probe.DeviceId.ToString(),
        probe.AgentGroupId.ToString(), probe.Type.ToString(), probe.IntervalSeconds, probe.TimeoutMilliseconds,
        probe.AttemptCount, probe.WarningRttMilliseconds, probe.CriticalRttMilliseconds, probe.FailureThreshold,
        probe.RecoveryThreshold, probe.Enabled, probe.ConfigVersion, probe.RowVersion);
    private static MaintenanceWindowResponse ToResponse(MaintenanceWindow window) => new(window.Id.ToString(), window.Name,
        window.StartsAt, window.EndsAt, window.Timezone, window.SiteId?.ToString(), window.DeviceId?.ToString(),
        window.ProbeId?.ToString(), window.Enabled, window.CreatedAt, window.UpdatedAt, window.RowVersion);
}
