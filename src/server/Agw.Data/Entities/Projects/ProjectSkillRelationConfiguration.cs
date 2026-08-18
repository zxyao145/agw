using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectSkillRelationConfiguration : IEntityTypeConfiguration<ProjectSkillRelation>
{
    public void Configure(EntityTypeBuilder<ProjectSkillRelation> builder)
    {
        builder.HasKey(e => new { e.ProjectId, e.SkillId });

        builder
            .HasOne(e => e.Project)
            .WithMany(project => project.ProjectSkillRelations)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Skill).WithMany().HasForeignKey(e => e.SkillId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.SkillId);
    }
}
