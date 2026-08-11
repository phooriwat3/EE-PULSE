using EePulse.Domain.Common;

namespace EePulse.Domain.Inventory;

public sealed class AgentGroup
{
    private AgentGroup()
    {
    }

    public AgentGroup(Guid id, string name, string? description, DateTimeOffset now)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException(nameof(id), "Agent group id is required.") : id;
        Name = Guard.Required(name, nameof(name), 200);
        Description = Guard.Optional(description, nameof(description), 1_000);
        Enabled = true;
        CreatedAt = Guard.Utc(now, nameof(now));
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long RowVersion { get; private set; }
    public long ConfigurationVersion { get; private set; }

    public void Update(string name, string? description, bool enabled, DateTimeOffset now)
    {
        Name = Guard.Required(name, nameof(name), 200);
        Description = Guard.Optional(description, nameof(description), 1_000);
        Enabled = enabled;
        UpdatedAt = Guard.Utc(now, nameof(now));
    }

    public long PublishConfiguration()
    {
        ConfigurationVersion = checked(ConfigurationVersion + 1);
        return ConfigurationVersion;
    }
}
