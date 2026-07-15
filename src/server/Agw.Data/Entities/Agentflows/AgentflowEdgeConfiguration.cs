using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agentflows;

public class AgentflowEdgeConfiguration : IEntityTypeConfiguration<AgentflowEdge>
{
    public void Configure(EntityTypeBuilder<AgentflowEdge> builder)
    {
        builder.HasKey(e => new { e.AgentflowId, e.EdgeId });
        builder.Property(e => e.Kind).HasConversion<int>();
        builder.Property(e => e.Label).HasMaxLength(200);
        builder.Property(e => e.ConditionJson).HasMaxLength(8000);
        builder.Property(e => e.ConfigJson).HasMaxLength(16000);

        builder.HasOne(e => e.SourceNode)
            .WithMany(n => n.SourceEdges)
            .HasForeignKey(e => new { e.AgentflowId, e.SourceNodeId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.TargetNode)
            .WithMany(n => n.TargetEdges)
            .HasForeignKey(e => new { e.AgentflowId, e.TargetNodeId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
