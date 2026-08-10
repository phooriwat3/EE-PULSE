using EePulse.Domain.Common;

namespace EePulse.Domain.Inventory;

public sealed class Site
{
    private Site()
    {
    }

    public Site(Guid id, string code, string name, string timezone, DateTimeOffset now)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException(nameof(id), "Site id is required.") : id;
        Code = NormalizeCode(code);
        Name = Guard.Required(name, nameof(name), 200);
        Timezone = Guard.Required(timezone, nameof(timezone), 100);
        Enabled = true;
        CreatedAt = Guard.Utc(now, nameof(now));
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Timezone { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long RowVersion { get; private set; }

    public void Update(string code, string name, string timezone, bool enabled, DateTimeOffset now)
    {
        Code = NormalizeCode(code);
        Name = Guard.Required(name, nameof(name), 200);
        Timezone = Guard.Required(timezone, nameof(timezone), 100);
        Enabled = enabled;
        UpdatedAt = Guard.Utc(now, nameof(now));
    }

    private static string NormalizeCode(string code) => Guard.Required(code, nameof(code), 50).ToUpperInvariant();
}
