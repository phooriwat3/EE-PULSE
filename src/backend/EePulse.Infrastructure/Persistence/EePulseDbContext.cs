using EePulse.Domain.Auditing;
using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
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
    public DbSet<ProbeResultLedgerEntry> ProbeResultLedgerEntries => Set<ProbeResultLedgerEntry>();
    public DbSet<ProbeStatusProjection> ProbeStatusProjections => Set<ProbeStatusProjection>();
    public DbSet<ProbeStatusPolicySnapshot> ProbeStatusPolicySnapshots => Set<ProbeStatusPolicySnapshot>();
    public DbSet<ProbeStatusPolicyBinding> ProbeStatusPolicyBindings => Set<ProbeStatusPolicyBinding>();
    public DbSet<AgentConfigurationEffectiveBoundary> AgentConfigurationEffectiveBoundaries => Set<AgentConfigurationEffectiveBoundary>();
    public DbSet<ProbeResultProcessingDisposition> ProbeResultProcessingDispositions => Set<ProbeResultProcessingDisposition>();
    public DbSet<ProbeFreshnessExpiryCause> ProbeFreshnessExpiryCauses => Set<ProbeFreshnessExpiryCause>();
    public DbSet<ProbeFreshnessExpiryCauseDisposition> ProbeFreshnessExpiryCauseDispositions => Set<ProbeFreshnessExpiryCauseDisposition>();
    public DbSet<ProbeFreshnessExpiryCauseTransition> ProbeFreshnessExpiryCauseTransitions => Set<ProbeFreshnessExpiryCauseTransition>();
    public DbSet<ProbeResultStatusTransition> ProbeResultStatusTransitions => Set<ProbeResultStatusTransition>();
    public DbSet<AvailabilityIncident> AvailabilityIncidents => Set<AvailabilityIncident>();
    public DbSet<IncidentLifecycleEvent> IncidentLifecycleEvents => Set<IncidentLifecycleEvent>();
    public DbSet<NotificationSuppressionContext> NotificationSuppressionContexts => Set<NotificationSuppressionContext>();

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
        RejectChanges(ChangeTracker.Entries<AgentConfigurationSnapshot>(), "Configuration snapshots are immutable.");
        RejectChanges(ChangeTracker.Entries<AgentConfigurationAcknowledgement>(), "Configuration acknowledgements are append-only.");
        RejectChanges(ChangeTracker.Entries<ProbeResultLedgerEntry>(), "Probe result ledger entries are immutable.");
        RejectChanges(ChangeTracker.Entries<ProbeStatusPolicySnapshot>(), "Status policy snapshots are immutable.");
        RejectChanges(ChangeTracker.Entries<ProbeStatusPolicyBinding>(), "Status policy bindings are immutable.");
        RejectChanges(ChangeTracker.Entries<AgentConfigurationEffectiveBoundary>(), "Configuration effective boundaries are immutable.");
        RejectChanges(ChangeTracker.Entries<ProbeResultProcessingDisposition>(), "Probe result processing dispositions are immutable.");
        RejectChanges(ChangeTracker.Entries<ProbeFreshnessExpiryCause>(), "Probe freshness expiry causes are immutable.");
        RejectChanges(ChangeTracker.Entries<ProbeFreshnessExpiryCauseDisposition>(), "Probe freshness expiry cause dispositions are immutable.");
        RejectChanges(ChangeTracker.Entries<ProbeFreshnessExpiryCauseTransition>(), "Probe freshness expiry cause transitions are immutable.");
        RejectChanges(ChangeTracker.Entries<ProbeResultStatusTransition>(), "Probe result status transitions are immutable.");
        RejectChanges(ChangeTracker.Entries<IncidentLifecycleEvent>(), "Incident lifecycle events are immutable.");
        RejectChanges(ChangeTracker.Entries<NotificationSuppressionContext>(), "Notification suppression contexts are immutable.");

        IncrementVersion(ChangeTracker.Entries<Site>());
        IncrementVersion(ChangeTracker.Entries<Device>());
        IncrementVersion(ChangeTracker.Entries<AgentGroup>());
        IncrementVersion(ChangeTracker.Entries<Probe>());
        IncrementVersion(ChangeTracker.Entries<MaintenanceWindow>());
        IncrementVersion(ChangeTracker.Entries<Agent>());
        IncrementVersion(ChangeTracker.Entries<AgentEnrollmentToken>());
        IncrementStateVersion(ChangeTracker.Entries<ProbeStatusProjection>());
    }

    private static void RejectChanges<TEntity>(IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>> entries, string message) where TEntity : class
    { if (entries.Any(entry => entry.State is EntityState.Modified or EntityState.Deleted)) throw new InvalidOperationException(message); }

    private static void IncrementVersion<TEntity>(IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>> entries)
        where TEntity : class
    {
        foreach (var entry in entries.Where(candidate => candidate.State is EntityState.Added or EntityState.Modified))
        {
            var version = entry.Property<long>("RowVersion");
            version.CurrentValue = entry.State == EntityState.Added ? 1 : version.OriginalValue + 1;
        }
    }

    private static void IncrementStateVersion(IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<ProbeStatusProjection>> entries)
    {
        foreach (var entry in entries.Where(candidate => candidate.State is EntityState.Added or EntityState.Modified))
        {
            var version = entry.Property<long>(nameof(ProbeStatusProjection.StateVersion));
            version.CurrentValue = entry.State == EntityState.Added ? 0 : version.OriginalValue + 1;
        }
    }
}
