using EePulse.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("sites");
        builder.HasKey(site => site.Id);
        builder.Property(site => site.Id).HasColumnName("id");
        builder.Property(site => site.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        builder.Property(site => site.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(site => site.Timezone).HasColumnName("timezone").HasMaxLength(100).IsRequired();
        builder.Property(site => site.Enabled).HasColumnName("enabled");
        builder.Property(site => site.CreatedAt).HasColumnName("created_at");
        builder.Property(site => site.UpdatedAt).HasColumnName("updated_at");
        builder.Property(site => site.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        builder.HasIndex(site => site.Code).IsUnique().HasDatabaseName("ux_sites_code");
    }
}
