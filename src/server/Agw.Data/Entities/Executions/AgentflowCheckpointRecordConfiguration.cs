using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Executions;

public sealed class AgentflowCheckpointRecordConfiguration : IEntityTypeConfiguration<AgentflowCheckpointRecord>
{
    public void Configure(EntityTypeBuilder<AgentflowCheckpointRecord> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ContextId).HasMaxLength(64).IsRequired();
        builder.Property(item => item.UserId).HasMaxLength(128).IsRequired();
        builder.Property(item => item.DefinitionFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(item => item.MarkersJson).IsRequired();
        builder.Property(item => item.CheckpointJson).IsRequired();
        builder.HasIndex(item => item.SourceExecutionId);
        builder.HasIndex(item => new
        {
            item.ProjectConversationId,
            item.AgentflowId,
            item.BoundarySequence,
        });
        builder
            .HasOne<ProjectConversation>()
            .WithMany()
            .HasForeignKey(item => item.ProjectConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
