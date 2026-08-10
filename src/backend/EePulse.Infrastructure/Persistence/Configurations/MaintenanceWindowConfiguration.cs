using EePulse.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class MaintenanceWindowConfiguration : IEntityTypeConfiguration<MaintenanceWindow>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        builder.ToTable("maintenance_windows", table => table.HasCheckConstraint(
            "ck_maintenance_windows_one_scope",
            "num_nonnulls(site_id, device_id, probe_id) = 1"));
        builder.HasKey(window => window.Id);
        builder.Property(window => window.Id).HasColumnName("id");
        builder.Property(window => window.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(window => window.StartsAt).HasColumnName("starts_at");
        builder.Property(window => window.EndsAt).HasColumnName("ends_at");
        builder.Property(window => window.Timezone).HasColumnName("timezone").HasMaxLength(100).IsRequired();
        builder.Property(window => window.SiteId).HasColumnName("site_id");
        builder.Property(window => window.DeviceId).HasColumnName("device_id");
        builder.Property(window => window.ProbeId).HasColumnName("probe_id");
        builder.Property(window => window.Enabled).HasColumnName("enabled");
        builder.Property(window => window.CreatedAt).HasColumnName("created_at");
        builder.Property(window => window.UpdatedAt).HasColumnName("updated_at");
        builder.Property(window => window.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        builder.HasOne<Site>().WithMany().HasForeignKey(window => window.SiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Device>().WithMany().HasForeignKey(window => window.DeviceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Probe>().WithMany().HasForeignKey(window => window.ProbeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(window => new { window.StartsAt, window.EndsAt }).HasDatabaseName("ix_maintenance_windows_range");
    }
}
