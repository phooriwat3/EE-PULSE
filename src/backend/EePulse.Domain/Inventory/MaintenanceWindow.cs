using EePulse.Domain.Common;

namespace EePulse.Domain.Inventory;

public sealed class MaintenanceWindow
{
    private MaintenanceWindow()
    {
    }

    public MaintenanceWindow(
        Guid id,
        string name,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string timezone,
        Guid? siteId,
        Guid? deviceId,
        Guid? probeId,
        DateTimeOffset now)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException(nameof(id), "Maintenance window id is required.") : id;
        ApplyConfiguration(name, startsAt, endsAt, timezone, siteId, deviceId, probeId);
        Enabled = true;
        CreatedAt = Guard.Utc(now, nameof(now));
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public string Timezone { get; private set; } = string.Empty;
    public Guid? SiteId { get; private set; }
    public Guid? DeviceId { get; private set; }
    public Guid? ProbeId { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long RowVersion { get; private set; }

    public void Update(
        string name,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string timezone,
        Guid? siteId,
        Guid? deviceId,
        Guid? probeId,
        bool enabled,
        DateTimeOffset now)
    {
        ApplyConfiguration(name, startsAt, endsAt, timezone, siteId, deviceId, probeId);
        Enabled = enabled;
        UpdatedAt = Guard.Utc(now, nameof(now));
    }

    private void ApplyConfiguration(
        string name,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string timezone,
        Guid? siteId,
        Guid? deviceId,
        Guid? probeId)
    {
        Name = Guard.Required(name, nameof(name), 200);
        StartsAt = Guard.Utc(startsAt, nameof(startsAt));
        EndsAt = Guard.Utc(endsAt, nameof(endsAt));
        Timezone = Guard.Required(timezone, nameof(timezone), 100);
        if (endsAt <= startsAt)
        {
            throw new DomainValidationException(nameof(endsAt), "Maintenance window end must be after its start.");
        }

        var scopeCount = new[] { siteId, deviceId, probeId }.Count(value => value.HasValue);
        if (scopeCount != 1 || new[] { siteId, deviceId, probeId }.Any(value => value == Guid.Empty))
        {
            throw new DomainValidationException(nameof(siteId), "A maintenance window must target exactly one valid Site, Device, or Probe.");
        }

        SiteId = siteId;
        DeviceId = deviceId;
        ProbeId = probeId;
    }
}
