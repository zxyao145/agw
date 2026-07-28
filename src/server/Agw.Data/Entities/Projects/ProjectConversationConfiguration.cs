using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectConversationConfiguration : IEntityTypeConfiguration<ProjectConversation>
{
    public void Configure(EntityTypeBuilder<ProjectConversation> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.JobId);
        builder.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(200).HasDefaultValue("Untitled");

        builder.HasIndex(e => new { e.ProjectId, e.ContextId }).IsUnique();
        builder.HasIndex(e => e.ProjectId);
        builder.HasIndex(e => e.JobId);
        builder.HasIndex(e => e.UpdateTime);

        builder.HasOne(e => e.Project)
            .WithMany(project => project.Conversations)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
