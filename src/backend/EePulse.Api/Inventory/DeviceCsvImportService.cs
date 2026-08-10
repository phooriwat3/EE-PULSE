using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EePulse.Application.Time;
using EePulse.Contracts.Inventory;
using EePulse.Domain.Auditing;
using EePulse.Domain.Common;
using EePulse.Domain.Inventory;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EePulse.Api.Inventory;

public sealed class DeviceCsvImportService
{
    public const int MaximumBytes = 1_048_576;
    public const int MaximumRows = 1_000;
    public const int MaximumCachedPreviews = 32;
    public const int MaximumCachedBytes = 8 * MaximumBytes;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly string[] RequiredHeaders =
        ["siteCode", "name", "address", "hostname", "deviceType", "area", "owner", "criticality", "tags"];
    private readonly ConcurrentDictionary<Guid, PreviewState> _previews = new();
    private readonly object _cacheLock = new();
    private long _cachedBytes;

    public async Task<CsvImportPreviewResponse> PreviewAsync(
        string csv, Guid actorId, EePulseDbContext db, IUtcClock clock, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(csv) > MaximumBytes)
        {
            throw new DomainValidationException(nameof(csv), $"CSV content must not exceed {MaximumBytes} bytes.");
        }

        var records = Parse(csv);
        if (records.Count == 0)
        {
            throw new DomainValidationException(nameof(csv), "CSV must contain a header row.");
        }

        var header = records[0];
        if (header.Count != RequiredHeaders.Length ||
            !header.Select(value => value.Trim()).SequenceEqual(RequiredHeaders, StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(nameof(csv), $"CSV headers must be: {string.Join(',', RequiredHeaders)}.");
        }

        if (records.Count - 1 > MaximumRows)
        {
            throw new DomainValidationException(nameof(csv), $"CSV must not contain more than {MaximumRows} data rows.");
        }

        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(site => site.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var existing = (await db.Devices.AsNoTracking().Where(device => device.Enabled)
                .Select(device => new { device.SiteId, device.Address }).ToListAsync(cancellationToken))
            .Select(item => $"{item.SiteId:N}|{item.Address}").ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var responseRows = new List<CsvImportPreviewRow>();
        var validRows = new List<PendingDevice>();

        for (var index = 1; index < records.Count; index++)
        {
            var record = records[index];
            var errors = new List<CsvImportError>();
            DeviceImportRow? normalized = null;
            if (record.Count != RequiredHeaders.Length)
            {
                errors.Add(new CsvImportError("row", "column_count", $"Expected {RequiredHeaders.Length} columns but received {record.Count}."));
            }
            else
            {
                var siteCode = record[0].Trim().ToUpperInvariant();
                if (!sites.TryGetValue(siteCode, out var site))
                {
                    errors.Add(new CsvImportError("siteCode", "site_not_found", "Site code does not exist."));
                }
                else
                {
                    try
                    {
                        var criticality = Enum.TryParse<Criticality>(record[7], true, out var parsed) && Enum.IsDefined(parsed)
                            ? parsed
                            : throw new DomainValidationException("criticality", "Criticality must be Low, Normal, High, or Critical.");
                        var tags = record[8].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var candidate = new Device(Guid.NewGuid(), site.Id, record[1], record[2], NullIfEmpty(record[3]),
                            record[4], NullIfEmpty(record[5]), NullIfEmpty(record[6]), criticality, tags, clock.UtcNow);
                        normalized = new DeviceImportRow(site.Code, candidate.Name, candidate.Address, candidate.Hostname,
                            candidate.DeviceType, candidate.Area, candidate.Owner, candidate.Criticality.ToString(), candidate.Tags.ToArray());
                        var duplicateKey = $"{site.Id:N}|{candidate.Address}";
                        if (existing.Contains(duplicateKey) || !seen.Add(duplicateKey))
                        {
                            errors.Add(new CsvImportError("address", "duplicate_site_address", "Address already exists within this Site or preview."));
                        }
                        else
                        {
                            validRows.Add(new PendingDevice(site.Id, normalized));
                        }
                    }
                    catch (DomainValidationException exception)
                    {
                        errors.Add(new CsvImportError(exception.Field, "validation", exception.Message));
                    }
                }
            }

            responseRows.Add(new CsvImportPreviewRow(index + 1, normalized, errors));
        }

        var token = Guid.NewGuid();
        var expiresAt = clock.UtcNow.Add(Lifetime);
        var response = new CsvImportPreviewResponse(token.ToString(), expiresAt, responseRows.Count,
            responseRows.Count(row => row.Errors.Count == 0), responseRows.Count(row => row.Errors.Count != 0), responseRows);
        AddPreview(token, new PreviewState(clock.UtcNow, expiresAt, actorId, Encoding.UTF8.GetByteCount(csv), validRows, responseRows), clock.UtcNow);
        return response;
    }

    public async Task<CsvImportCommitResponse> CommitAsync(
        string previewToken, EePulseDbContext db, IUtcClock clock, HttpContext http, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(previewToken, out var token) || !_previews.TryGetValue(token, out var state))
        {
            throw new DomainValidationException(nameof(previewToken), "Preview token is invalid or no longer available.");
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Committed is not null)
            {
                return state.Committed with { AlreadyCommitted = true };
            }

            if (clock.UtcNow >= state.ExpiresAt)
            {
                Remove(token);
                throw new DomainValidationException(nameof(previewToken), "Preview token has expired.");
            }

            var actorText = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            Guid? actorId = Guid.TryParse(actorText, out var actor) && actor != Guid.Empty ? actor : null;
            if (actorId != state.ActorId)
            {
                throw new UnauthorizedAccessException("Preview tokens can only be committed by the actor who created them.");
            }
            var createdIds = new List<string>();
            foreach (var pending in state.ValidRows)
            {
                var row = pending.Row;
                var device = new Device(Guid.NewGuid(), pending.SiteId, row.Name, row.Address, row.Hostname, row.DeviceType,
                    row.Area, row.Owner, Enum.Parse<Criticality>(row.Criticality), row.Tags, clock.UtcNow);
                db.Devices.Add(device);
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), actorId, "inventory.device.imported", "Device", device.Id,
                    null, JsonSerializer.Serialize(row), http.TraceIdentifier, clock.UtcNow,
                    http.Connection.RemoteIpAddress?.ToString()));
                createdIds.Add(device.Id.ToString());
            }

            await db.SaveChangesAsync(cancellationToken);
            state.Committed = new CsvImportCommitResponse(token.ToString(), createdIds.Count,
                state.Rows.Count(row => row.Errors.Count != 0), createdIds,
                state.Rows.Where(row => row.Errors.Count != 0).ToArray(), false);
            return state.Committed;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static List<List<string>> Parse(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => value.Length != 0)) rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted) throw new DomainValidationException(nameof(csv), "CSV contains an unterminated quoted field.");
        row.Add(field.ToString());
        if (row.Any(value => value.Length != 0)) rows.Add(row);
        return rows;
    }

    private void AddPreview(Guid token, PreviewState state, DateTimeOffset now)
    {
        lock (_cacheLock)
        {
            RemoveExpiredUnsafe(now);
            if (_previews.Count >= MaximumCachedPreviews || _cachedBytes + state.SizeBytes > MaximumCachedBytes)
            {
                throw new PreviewCapacityException("The CSV preview cache is full. Retry after an existing preview expires.");
            }

            _previews[token] = state;
            _cachedBytes += state.SizeBytes;
        }
    }

    private void Remove(Guid token)
    {
        lock (_cacheLock) RemoveUnsafe(token);
    }

    private void RemoveExpiredUnsafe(DateTimeOffset now)
    {
        foreach (var preview in _previews.Where(item => item.Value.ExpiresAt <= now).ToArray()) RemoveUnsafe(preview.Key);
    }

    private void RemoveUnsafe(Guid token)
    {
        if (_previews.TryRemove(token, out var removed)) _cachedBytes -= removed.SizeBytes;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private sealed record PendingDevice(Guid SiteId, DeviceImportRow Row);
    private sealed class PreviewState(
        DateTimeOffset createdAt, DateTimeOffset expiresAt, Guid actorId, int sizeBytes,
        List<PendingDevice> validRows, List<CsvImportPreviewRow> rows)
    {
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public Guid ActorId { get; } = actorId;
        public int SizeBytes { get; } = sizeBytes;
        public List<PendingDevice> ValidRows { get; } = validRows;
        public List<CsvImportPreviewRow> Rows { get; } = rows;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CsvImportCommitResponse? Committed { get; set; }
    }
}

public sealed class PreviewCapacityException(string message) : InvalidOperationException(message);
