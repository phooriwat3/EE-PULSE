using System.Text.Json;
using System.Net;
using EePulse.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices");
        builder.HasKey(device => device.Id);
        builder.Property(device => device.Id).HasColumnName("id");
        builder.Property(device => device.SiteId).HasColumnName("site_id");
        builder.Property(device => device.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(device => device.Address)
            .HasColumnName("address")
            .HasColumnType("inet")
            .HasConversion(address => IPAddress.Parse(address), address => address.ToString())
            .IsRequired();
        builder.Property(device => device.Hostname).HasColumnName("hostname").HasMaxLength(253);
        builder.Property(device => device.DeviceType).HasColumnName("device_type").HasMaxLength(100).IsRequired();
        builder.Property(device => device.Area).HasColumnName("area").HasMaxLength(200);
        builder.Property(device => device.Owner).HasColumnName("owner").HasMaxLength(200);
        builder.Property(device => device.Criticality).HasColumnName("criticality").HasConversion<string>().HasMaxLength(20);
        builder.Property(device => device.Enabled).HasColumnName("enabled");
        builder.Property(device => device.CreatedAt).HasColumnName("created_at");
        builder.Property(device => device.UpdatedAt).HasColumnName("updated_at");
        builder.Property(device => device.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        builder.Ignore(device => device.Tags);
        builder.Property<List<string>>("_tags")
            .HasColumnName("tags")
            .HasColumnType("jsonb")
            .HasConversion(
                tags => JsonSerializer.Serialize(tags, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<List<string>>(json, JsonSerializerOptions.Default) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    tags => tags.Aggregate(0, (hash, tag) => HashCode.Combine(hash, tag.GetHashCode(StringComparison.Ordinal))),
                    tags => tags.ToList()));
        builder.HasOne<Site>().WithMany().HasForeignKey(device => device.SiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(device => new { device.SiteId, device.Address })
            .IsUnique()
            .HasFilter("\"enabled\"")
            .HasDatabaseName("ux_devices_site_address");
        builder.HasIndex(device => new { device.SiteId, device.Enabled }).HasDatabaseName("ix_devices_site_enabled");
    }
}
