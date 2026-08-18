using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectConnectionRelationConfiguration : IEntityTypeConfiguration<ProjectConnectionRelation>
{
    public void Configure(EntityTypeBuilder<ProjectConnectionRelation> builder)
    {
        builder.ToTable(table => table.HasComment("Binds a project to an integration connection."));
        builder.HasKey(entity => new { entity.ProjectId, entity.ConnectionId });

        builder
            .HasOne(entity => entity.Project)
            .WithMany(project => project.ProjectConnectionRelations)
            .HasForeignKey(entity => entity.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(entity => entity.Connection)
            .WithMany()
            .HasForeignKey(entity => entity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.ConnectionId);
    }
}
