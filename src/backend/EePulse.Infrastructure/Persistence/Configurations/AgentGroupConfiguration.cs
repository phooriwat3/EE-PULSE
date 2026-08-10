using EePulse.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class AgentGroupConfiguration : IEntityTypeConfiguration<AgentGroup>
{
    public void Configure(EntityTypeBuilder<AgentGroup> builder)
    {
        builder.ToTable("agent_groups");
        builder.HasKey(group => group.Id);
        builder.Property(group => group.Id).HasColumnName("id");
        builder.Property(group => group.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(group => group.Description).HasColumnName("description").HasMaxLength(1_000);
        builder.Property(group => group.Enabled).HasColumnName("enabled");
        builder.Property(group => group.CreatedAt).HasColumnName("created_at");
        builder.Property(group => group.UpdatedAt).HasColumnName("updated_at");
        builder.Property(group => group.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        builder.HasIndex(group => group.Name).IsUnique().HasDatabaseName("ux_agent_groups_name");
    }
}
