using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agentflows;

public class AgentflowEdgeConfiguration : IEntityTypeConfiguration<AgentflowEdge>
{
    public void Configure(EntityTypeBuilder<AgentflowEdge> builder)
    {
        builder.HasKey(e => new
        {
            e.AgentflowId,
            e.AgentflowRevision,
            e.EdgeId,
        });
        builder.Property(e => e.Label).HasMaxLength(200);
        builder.Property(e => e.ConditionJson).HasMaxLength(8000).HasDefaultValue("[]").IsRequired();
        builder.Property(e => e.ChoiceId).HasMaxLength(64);

        builder
            .HasOne(e => e.SourceNode)
            .WithMany(n => n.SourceEdges)
            .HasForeignKey(e => new
            {
                e.AgentflowId,
                e.AgentflowRevision,
                e.SourceNodeId,
            })
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(e => e.TargetNode)
            .WithMany(n => n.TargetEdges)
            .HasForeignKey(e => new
            {
                e.AgentflowId,
                e.AgentflowRevision,
                e.TargetNodeId,
            })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
