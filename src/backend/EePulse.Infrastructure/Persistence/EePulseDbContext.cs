using EePulse.Domain.Auditing;
using EePulse.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace EePulse.Infrastructure.Persistence;

public sealed class EePulseDbContext(DbContextOptions<EePulseDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<AgentGroup> AgentGroups => Set<AgentGroup>();
    public DbSet<Probe> Probes => Set<Probe>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EePulseDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareForSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareForSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareForSave()
    {
        foreach (var entry in ChangeTracker.Entries<AuditEvent>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Audit events are append-only and cannot be modified or deleted.");
            }
        }

        IncrementVersion(ChangeTracker.Entries<Site>());
        IncrementVersion(ChangeTracker.Entries<Device>());
        IncrementVersion(ChangeTracker.Entries<AgentGroup>());
        IncrementVersion(ChangeTracker.Entries<Probe>());
        IncrementVersion(ChangeTracker.Entries<MaintenanceWindow>());
    }

    private static void IncrementVersion<TEntity>(IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>> entries)
        where TEntity : class
    {
        foreach (var entry in entries.Where(candidate => candidate.State is EntityState.Added or EntityState.Modified))
        {
            var version = entry.Property<long>("RowVersion");
            version.CurrentValue = entry.State == EntityState.Added ? 1 : version.OriginalValue + 1;
        }
    }
}
