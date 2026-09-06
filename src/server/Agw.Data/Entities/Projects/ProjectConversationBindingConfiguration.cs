using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectConversationBindingConfiguration : IEntityTypeConfiguration<ProjectConversationBinding>
{
    public void Configure(EntityTypeBuilder<ProjectConversationBinding> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ExternalAgentName).IsRequired().HasMaxLength(200);
        builder.Property(e => e.ProviderSessionId).IsRequired().HasMaxLength(200);

        builder
            .HasIndex(e => new
            {
                e.ProjectConversationId,
                e.AgentId,
                e.ExternalAgentName,
            })
            .IsUnique();
        builder.HasIndex(e => new { e.ExternalAgentName, e.ProviderSessionId });

        builder
            .HasOne(e => e.ProjectConversation)
            .WithMany()
            .HasForeignKey(e => e.ProjectConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
