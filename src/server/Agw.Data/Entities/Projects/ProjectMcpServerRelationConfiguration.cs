using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectMcpServerRelationConfiguration : IEntityTypeConfiguration<ProjectMcpServerRelation>
{
    public void Configure(EntityTypeBuilder<ProjectMcpServerRelation> builder)
    {
        builder.HasKey(e => new { e.ProjectId, e.McpToolServerId });

        builder.HasOne(e => e.Project)
            .WithMany(project => project.ProjectMcpToolServers)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.McpToolServer)
            .WithMany()
            .HasForeignKey(e => e.McpToolServerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.McpToolServerId);
    }
}
