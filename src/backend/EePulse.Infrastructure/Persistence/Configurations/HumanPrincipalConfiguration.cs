using EePulse.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class HumanPrincipalConfiguration : IEntityTypeConfiguration<HumanPrincipal>
{
    public void Configure(EntityTypeBuilder<HumanPrincipal> builder)
    {
        builder.ToTable("human_principals", table =>
        {
            table.HasCheckConstraint("ck_human_principals_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_human_principals_issuer_shape", PrincipalComponentConstraint("issuer"));
            table.HasCheckConstraint("ck_human_principals_subject_shape", PrincipalComponentConstraint("subject"));
        });
        builder.HasKey(principal => principal.Id).HasName("pk_human_principals");
        builder.HasIndex(principal => new { principal.Issuer, principal.Subject })
            .IsUnique().HasDatabaseName("ux_human_principals_issuer_subject");
        builder.Property(principal => principal.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(principal => principal.Issuer).HasColumnName("issuer")
            .UseCollation("C").HasMaxLength(HumanPrincipal.MaximumIssuerLength).IsRequired()
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(principal => principal.Subject).HasColumnName("subject")
            .UseCollation("C").HasMaxLength(HumanPrincipal.MaximumSubjectLength).IsRequired()
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(principal => principal.CreatedAt).HasColumnName("created_at")
            .HasColumnType("timestamp with time zone").IsRequired()
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }

    private static string PrincipalComponentConstraint(string column) =>
        $"char_length({column}) BETWEEN 1 AND 512 AND {column} = btrim({column}, ' ' || chr(9) || chr(10) || chr(11) || chr(12) || chr(13) || chr(160) || chr(5760) || chr(8192) || chr(8193) || chr(8194) || chr(8195) || chr(8196) || chr(8197) || chr(8198) || chr(8199) || chr(8200) || chr(8201) || chr(8202) || chr(8232) || chr(8233) || chr(8239) || chr(8287) || chr(12288)) AND position(chr(8232) in {column}) = 0 AND position(chr(8233) in {column}) = 0 AND {column} !~ '[[:cntrl:]]' AND translate({column}, chr(128) || chr(129) || chr(130) || chr(131) || chr(132) || chr(133) || chr(134) || chr(135) || chr(136) || chr(137) || chr(138) || chr(139) || chr(140) || chr(141) || chr(142) || chr(143) || chr(144) || chr(145) || chr(146) || chr(147) || chr(148) || chr(149) || chr(150) || chr(151) || chr(152) || chr(153) || chr(154) || chr(155) || chr(156) || chr(157) || chr(158) || chr(159), '') = {column}";
}
