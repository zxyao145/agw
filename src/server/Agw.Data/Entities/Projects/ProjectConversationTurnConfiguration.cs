using Agw.Agents.Contracts.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectConversationTurnConfiguration : IEntityTypeConfiguration<ProjectConversationTurn>
{
    public void Configure(EntityTypeBuilder<ProjectConversationTurn> builder)
    {
        builder.HasKey(e => e.Id);
        builder
            .Property(e => e.RuntimeType)
            .HasConversion(LowercaseEnum.Converter<AgentRuntimeType>())
            .HasMaxLength(16)
            .IsRequired();
        builder
            .Property(e => e.Status)
            .HasConversion(LowercaseEnum.Converter<ProjectConversationTurnStatus>())
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.HasIndex(e => new { e.ProjectConversationId, e.FirstSequence });
        builder.HasIndex(e => new { e.ProjectConversationId, e.Status });
        builder.HasIndex(e => e.TaskId);

        builder
            .HasOne(e => e.ProjectConversation)
            .WithMany()
            .HasForeignKey(e => e.ProjectConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
