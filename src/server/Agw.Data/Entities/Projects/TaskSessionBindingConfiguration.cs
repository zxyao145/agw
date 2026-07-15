using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class TaskSessionBindingConfiguration : IEntityTypeConfiguration<TaskSessionBinding>
{
    public void Configure(EntityTypeBuilder<TaskSessionBinding> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ExternalAgentName).IsRequired().HasMaxLength(200);
        builder.Property(e => e.ProviderSessionId).IsRequired().HasMaxLength(200);

        builder.HasIndex(e => new { e.ProjectContextId, e.AgentId, e.ExternalAgentName }).IsUnique();
        builder.HasIndex(e => new { e.ExternalAgentName, e.ProviderSessionId });

        builder.HasOne(e => e.ProjectContext)
            .WithMany()
            .HasForeignKey(e => e.ProjectContextId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
