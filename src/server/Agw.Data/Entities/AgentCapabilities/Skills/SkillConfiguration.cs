using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Skills;

public class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Name).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(1024);
        builder.Property(e => e.Kind).HasConversion<int>();
        builder.Property(e => e.ContentPath).IsRequired().HasMaxLength(500);
        builder.Property(e => e.RemoteUrl).HasMaxLength(2048);
    }
}
