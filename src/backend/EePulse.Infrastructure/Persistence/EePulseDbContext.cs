using EePulse.Domain.Auditing;
using EePulse.Domain.Agents;
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
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentAllowedNetwork> AgentAllowedNetworks => Set<AgentAllowedNetwork>();
    public DbSet<AgentPolicyAllowedNetwork> AgentPolicyAllowedNetworks => Set<AgentPolicyAllowedNetwork>();
    public DbSet<AgentGroupAllowedNetwork> AgentGroupAllowedNetworks => Set<AgentGroupAllowedNetwork>();
    public DbSet<AgentEnrollmentToken> AgentEnrollmentTokens => Set<AgentEnrollmentToken>();
    public DbSet<AgentEnrollmentTokenAllowedNetwork> AgentEnrollmentTokenAllowedNetworks => Set<AgentEnrollmentTokenAllowedNetwork>();
    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();
    public DbSet<AgentConfigurationSnapshot> AgentConfigurationSnapshots => Set<AgentConfigurationSnapshot>();
    public DbSet<AgentConfigurationAcknowledgement> AgentConfigurationAcknowledgements => Set<AgentConfigurationAcknowledgement>();
    public DbSet<AgentHeartbeatReceipt> AgentHeartbeatReceipts => Set<AgentHeartbeatReceipt>();

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
        RejectChanges(ChangeTracker.Entries<AgentConfigurationSnapshot>(),"Configuration snapshots are immutable.");
        RejectChanges(ChangeTracker.Entries<AgentConfigurationAcknowledgement>(),"Configuration acknowledgements are append-only.");

        IncrementVersion(ChangeTracker.Entries<Site>());
        IncrementVersion(ChangeTracker.Entries<Device>());
        IncrementVersion(ChangeTracker.Entries<AgentGroup>());
        IncrementVersion(ChangeTracker.Entries<Probe>());
        IncrementVersion(ChangeTracker.Entries<MaintenanceWindow>());
        IncrementVersion(ChangeTracker.Entries<Agent>());
        IncrementVersion(ChangeTracker.Entries<AgentEnrollmentToken>());
    }

    private static void RejectChanges<TEntity>(IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>> entries,string message) where TEntity:class
    {if(entries.Any(entry=>entry.State is EntityState.Modified or EntityState.Deleted))throw new InvalidOperationException(message);}

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
