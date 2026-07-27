using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Skills;

public class RemoteSkillCacheConfiguration : IEntityTypeConfiguration<RemoteSkillCache>
{
    public void Configure(EntityTypeBuilder<RemoteSkillCache> builder)
    {
        builder.HasKey(e => e.SkillId);
        builder.Property(e => e.SourceUrl).IsRequired().HasMaxLength(2048);
        builder.Property(e => e.ContentJson).IsRequired();
        builder.Property(e => e.FetchedAt).IsRequired();
        builder
            .HasOne<Skill>()
            .WithOne()
            .HasForeignKey<RemoteSkillCache>(e => e.SkillId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
