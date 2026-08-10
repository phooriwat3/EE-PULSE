using EePulse.Domain.Auditing;
using EePulse.Domain.Inventory;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using EePulse.Infrastructure.Time;

namespace EePulse.IntegrationTests;

public sealed class PostgreSqlPersistenceTests
{
    [Fact]
    public async Task ProductionReadinessDoesNotApplyMigrationsAndRejectsReachableEmptyDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(cancellationToken);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        using var client = factory.CreateClient();

        var beforeMigration = await client.GetAsync("/health/ready", cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, beforeMigration.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
        }

        var afterMigration = await client.GetAsync("/health/ready", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, afterMigration.StatusCode);
    }

    [Fact]
    public async Task MigrationPersistenceConstraintsAndConcurrencyWorkAgainstPostgreSql()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<EePulseDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using (var migrationContext = new EePulseDbContext(options))
        {
            await migrationContext.Database.MigrateAsync(cancellationToken);
            var appliedMigrations = await migrationContext.Database.GetAppliedMigrationsAsync(cancellationToken);
            Assert.Contains(appliedMigrations, migration => migration.EndsWith("_InitialInventory", StringComparison.Ordinal));
        }

        var now = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
        var site = new Site(Guid.NewGuid(), "BKK-01", "Bangkok", "Asia/Bangkok", now);
        var group = new AgentGroup(Guid.NewGuid(), "Bangkok Agents", null, now);
        var device = new Device(
            Guid.NewGuid(), site.Id, "PLC 1", "192.168.1.10", "plc-1.example.local", "PLC",
            "Line A", "EE", Criticality.High, ["production"], now);
        var probe = new Probe(Guid.NewGuid(), device.Id, group.Id, 30, 2_000, 3, 100, 200, 3, 2);
        var maintenance = new MaintenanceWindow(
            Guid.NewGuid(), "Site work", now.AddHours(1), now.AddHours(2), "Asia/Bangkok",
            site.Id, null, null, now);
        var audit = new AuditEvent(
            Guid.NewGuid(), null, "inventory.device.created", "Device", device.Id, null,
            "{\"name\":\"PLC 1\"}", "integration-test", now, "2001:db8::1");

        await using (var insertContext = new EePulseDbContext(options))
        {
            insertContext.AddRange(site, group, device, probe, maintenance, audit);
            await insertContext.SaveChangesAsync(cancellationToken);
            Assert.Equal(1, device.RowVersion);
            Assert.Equal(1, site.RowVersion);
        }

        await using (var readContext = new EePulseDbContext(options))
        {
            var stored = await readContext.Devices.SingleAsync(candidate => candidate.Id == device.Id, cancellationToken);
            Assert.Equal("192.168.1.10", stored.Address);
            Assert.Equal(["production"], stored.Tags);
            Assert.Equal(TimeSpan.Zero, stored.CreatedAt.Offset);
        }

        await using (var duplicateContext = new EePulseDbContext(options))
        {
            duplicateContext.Devices.Add(new Device(
                Guid.NewGuid(), site.Id, "Duplicate address", "192.168.1.10", null, "PLC",
                null, null, Criticality.Normal, [], now));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync(cancellationToken));
        }

        await using var firstContext = new EePulseDbContext(options);
        await using var secondContext = new EePulseDbContext(options);
        var first = await firstContext.Devices.SingleAsync(candidate => candidate.Id == device.Id, cancellationToken);
        var second = await secondContext.Devices.SingleAsync(candidate => candidate.Id == device.Id, cancellationToken);
        first.Update(site.Id, "PLC 1A", first.Address, first.Hostname, first.DeviceType, first.Area, first.Owner,
            first.Criticality, first.Tags, first.Enabled, now.AddMinutes(1));
        second.Update(site.Id, "PLC 1B", second.Address, second.Hostname, second.DeviceType, second.Area, second.Owner,
            second.Criticality, second.Tags, second.Enabled, now.AddMinutes(1));
        await firstContext.SaveChangesAsync(cancellationToken);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync(cancellationToken));

        await using (var disableContext = new EePulseDbContext(options))
        {
            var disabled = await disableContext.Devices.SingleAsync(candidate => candidate.Id == device.Id, cancellationToken);
            disabled.Update(disabled.SiteId, disabled.Name, disabled.Address, disabled.Hostname, disabled.DeviceType,
                disabled.Area, disabled.Owner, disabled.Criticality, disabled.Tags, false, now.AddMinutes(2));
            await disableContext.SaveChangesAsync(cancellationToken);
        }

        var otherSite = new Site(Guid.NewGuid(), "CNX-01", "Chiang Mai", "Asia/Bangkok", now);
        var replacement = new Device(Guid.NewGuid(), site.Id, "Replacement", device.Address, device.Hostname, "PLC",
            null, null, Criticality.Normal, [], now.AddMinutes(3));
        var crossSite = new Device(Guid.NewGuid(), otherSite.Id, "Cross-site", device.Address, device.Hostname, "PLC",
            null, null, Criticality.Normal, [], now.AddMinutes(3));
        await using (var reuseContext = new EePulseDbContext(options))
        {
            reuseContext.AddRange(otherSite, replacement, crossSite);
            await reuseContext.SaveChangesAsync(cancellationToken);
        }

        await using (var verifyReuseContext = new EePulseDbContext(options))
        {
            Assert.Equal(2, await verifyReuseContext.Devices.CountAsync(
                candidate => candidate.SiteId == site.Id && candidate.Address == device.Address, cancellationToken));
            Assert.Equal(3, await verifyReuseContext.Devices.CountAsync(
                candidate => candidate.Hostname == device.Hostname, cancellationToken));
            var retained = await verifyReuseContext.Devices.SingleAsync(candidate => candidate.Id == device.Id, cancellationToken);
            Assert.False(retained.Enabled);
            Assert.Equal(device.CreatedAt, retained.CreatedAt);
        }

        await using (var reenableContext = new EePulseDbContext(options))
        {
            var disabled = await reenableContext.Devices.SingleAsync(candidate => candidate.Id == device.Id, cancellationToken);
            disabled.Update(disabled.SiteId, disabled.Name, disabled.Address, disabled.Hostname, disabled.DeviceType,
                disabled.Area, disabled.Owner, disabled.Criticality, disabled.Tags, true, now.AddMinutes(4));
            await Assert.ThrowsAsync<DbUpdateException>(() => reenableContext.SaveChangesAsync(cancellationToken));
        }

        await using var concurrentFirstContext = new EePulseDbContext(options);
        await using var concurrentSecondContext = new EePulseDbContext(options);
        concurrentFirstContext.Devices.Add(new Device(Guid.NewGuid(), site.Id, "Concurrent A", "192.168.1.99", null,
            "PLC", null, null, Criticality.Normal, [], now));
        concurrentSecondContext.Devices.Add(new Device(Guid.NewGuid(), site.Id, "Concurrent B", "192.168.1.99", null,
            "PLC", null, null, Criticality.Normal, [], now));
        await concurrentFirstContext.SaveChangesAsync(cancellationToken);
        await Assert.ThrowsAsync<DbUpdateException>(() => concurrentSecondContext.SaveChangesAsync(cancellationToken));

        await using (var auditContext = new EePulseDbContext(options))
        {
            var storedAudit = await auditContext.AuditEvents.SingleAsync(candidate => candidate.Id == audit.Id, cancellationToken);
            auditContext.Entry(storedAudit).Property(candidate => candidate.Action).CurrentValue = "tampered";
            await Assert.ThrowsAsync<InvalidOperationException>(() => auditContext.SaveChangesAsync(cancellationToken));
        }

        await using (var seedContext = new EePulseDbContext(options))
        {
            await DevelopmentInventorySeeder.SeedAsync(seedContext, new SystemUtcClock(), cancellationToken);
            seedContext.ChangeTracker.Clear();
            await DevelopmentInventorySeeder.SeedAsync(seedContext, new SystemUtcClock(), cancellationToken);
            Assert.Equal(1, await seedContext.Sites.CountAsync(candidate => candidate.Code == "DEV", cancellationToken));
            Assert.Equal(1, await seedContext.Devices.CountAsync(candidate => candidate.Address == "192.0.2.10", cancellationToken));
            Assert.Equal(1, await seedContext.AuditEvents.CountAsync(
                candidate => candidate.Action == "development.inventory.seeded", cancellationToken));
        }
    }
}
