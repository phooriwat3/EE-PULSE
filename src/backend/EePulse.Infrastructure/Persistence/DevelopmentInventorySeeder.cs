using EePulse.Application.Time;
using EePulse.Domain.Auditing;
using EePulse.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace EePulse.Infrastructure.Persistence;

public static class DevelopmentInventorySeeder
{
    public static async Task SeedAsync(
        EePulseDbContext db,
        IUtcClock clock,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var site = await db.Sites.SingleOrDefaultAsync(candidate => candidate.Code == "DEV", cancellationToken);
        if (site is null)
        {
            site = new Site(Guid.Parse("10000000-0000-4000-8000-000000000001"), "DEV", "Development Site", "UTC", now);
            db.Sites.Add(site);
        }

        var group = await db.AgentGroups.SingleOrDefaultAsync(candidate => candidate.Name == "Development Agents", cancellationToken);
        if (group is null)
        {
            group = new AgentGroup(Guid.Parse("20000000-0000-4000-8000-000000000001"), "Development Agents",
                "Local fixtures only", now);
            db.AgentGroups.Add(group);
        }

        var device = await db.Devices.SingleOrDefaultAsync(
            candidate => candidate.SiteId == site.Id && candidate.Address == "192.0.2.10", cancellationToken);
        if (device is null)
        {
            device = new Device(Guid.Parse("30000000-0000-4000-8000-000000000001"), site.Id, "Development Target",
                "192.0.2.10", "target.example.test", "Test", "Lab", "Development", Criticality.Normal,
                ["development"], now);
            db.Devices.Add(device);
        }

        if (!await db.Probes.AnyAsync(candidate => candidate.DeviceId == device.Id, cancellationToken))
        {
            db.Probes.Add(new Probe(Guid.Parse("40000000-0000-4000-8000-000000000001"), device.Id, group.Id,
                30, 2_000, 3, 100, 250, 3, 2));
        }

        if (db.ChangeTracker.HasChanges())
        {
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), null, "development.inventory.seeded", "Inventory", null,
                null, "{\"fixture\":\"development-only\"}", "development-seed", now, null));
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
