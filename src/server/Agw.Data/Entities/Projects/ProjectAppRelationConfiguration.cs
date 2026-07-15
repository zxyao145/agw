using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectAppRelationConfiguration : IEntityTypeConfiguration<ProjectAppRelation>
{
    public void Configure(EntityTypeBuilder<ProjectAppRelation> builder)
    {
        builder.HasKey(e => new { e.ProjectId, e.AppInstanceId });

        builder.HasOne(e => e.Project)
            .WithMany(project => project.ProjectAppRelations)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.AppInstance)
            .WithMany()
            .HasForeignKey(e => e.AppInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.AppInstanceId);
    }
}
