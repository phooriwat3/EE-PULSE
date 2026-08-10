using EePulse.Domain.Common;

namespace EePulse.Domain.Inventory;

public sealed class Device
{
    private readonly List<string> _tags = [];

    private Device()
    {
    }

    public Device(
        Guid id,
        Guid siteId,
        string name,
        string address,
        string? hostname,
        string deviceType,
        string? area,
        string? owner,
        Criticality criticality,
        IEnumerable<string>? tags,
        DateTimeOffset now)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException(nameof(id), "Device id is required.") : id;
        SiteId = siteId == Guid.Empty ? throw new DomainValidationException(nameof(siteId), "Site id is required.") : siteId;
        ApplyConfiguration(name, address, hostname, deviceType, area, owner, criticality, tags);
        Enabled = true;
        CreatedAt = Guard.Utc(now, nameof(now));
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid SiteId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public string? Hostname { get; private set; }
    public string DeviceType { get; private set; } = string.Empty;
    public string? Area { get; private set; }
    public string? Owner { get; private set; }
    public Criticality Criticality { get; private set; }
    public IReadOnlyList<string> Tags => _tags;
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long RowVersion { get; private set; }

    public void Update(
        Guid siteId,
        string name,
        string address,
        string? hostname,
        string deviceType,
        string? area,
        string? owner,
        Criticality criticality,
        IEnumerable<string>? tags,
        bool enabled,
        DateTimeOffset now)
    {
        SiteId = siteId == Guid.Empty ? throw new DomainValidationException(nameof(siteId), "Site id is required.") : siteId;
        ApplyConfiguration(name, address, hostname, deviceType, area, owner, criticality, tags);
        Enabled = enabled;
        UpdatedAt = Guard.Utc(now, nameof(now));
    }

    private void ApplyConfiguration(
        string name,
        string address,
        string? hostname,
        string deviceType,
        string? area,
        string? owner,
        Criticality criticality,
        IEnumerable<string>? tags)
    {
        Name = Guard.Required(name, nameof(name), 200);
        Address = Guard.Ipv4(address, nameof(address));
        Hostname = Guard.Hostname(hostname, nameof(hostname));
        DeviceType = Guard.Required(deviceType, nameof(deviceType), 100);
        Area = Guard.Optional(area, nameof(area), 200);
        Owner = Guard.Optional(owner, nameof(owner), 200);
        if (!Enum.IsDefined(criticality))
        {
            throw new DomainValidationException(nameof(criticality), "Criticality is invalid.");
        }

        Criticality = criticality;

        var normalizedTags = (tags ?? [])
            .Select(tag => Guard.Required(tag, nameof(tags), 64).ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalizedTags.Length > 50)
        {
            throw new DomainValidationException(nameof(tags), "A device cannot have more than 50 tags.");
        }

        _tags.Clear();
        _tags.AddRange(normalizedTags);
    }
}
