using EePulse.Domain.Preferences;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class UserTimezonePreferenceConfiguration : IEntityTypeConfiguration<UserTimezonePreference>
{
    public void Configure(EntityTypeBuilder<UserTimezonePreference> builder)
    {
        builder.ToTable("user_timezone_preferences", table =>
        {
            table.HasCheckConstraint("ck_user_timezone_preferences_version", "version >= 1");
            table.HasCheckConstraint("ck_user_timezone_preferences_issuer", "char_length(issuer) BETWEEN 1 AND 512");
            table.HasCheckConstraint("ck_user_timezone_preferences_subject", "char_length(subject) BETWEEN 1 AND 512");
        });
        builder.HasKey(preference => new { preference.Issuer, preference.Subject });
        builder.Property(preference => preference.Issuer).HasColumnName("issuer").HasMaxLength(UserTimezonePreference.MaximumIssuerLength).IsRequired();
        builder.Property(preference => preference.Subject).HasColumnName("subject").HasMaxLength(UserTimezonePreference.MaximumSubjectLength).IsRequired();
        builder.Property(preference => preference.Timezone).HasColumnName("timezone").HasMaxLength(UserTimezonePreference.MaximumTimezoneLength);
        builder.Property(preference => preference.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(preference => preference.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(preference => preference.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
    }
}
