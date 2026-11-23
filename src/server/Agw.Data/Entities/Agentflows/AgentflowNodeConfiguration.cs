using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agentflows;

public class AgentflowNodeConfiguration : IEntityTypeConfiguration<AgentflowNode>
{
    public void Configure(EntityTypeBuilder<AgentflowNode> builder)
    {
        builder.HasKey(e => new
        {
            e.AgentflowId,
            e.AgentflowRevision,
            e.NodeId,
        });
        builder.Property(e => e.Kind).HasConversion<int>();
        builder.Property(e => e.RoutingStrategy).HasConversion<int>();
        builder.Property(e => e.Name).HasMaxLength(200);
        builder.Property(e => e.Instructions).HasMaxLength(8000);
        builder.Property(e => e.ConfigJson).HasMaxLength(16000).HasDefaultValue("{}").IsRequired();
        builder.HasIndex(e => new
        {
            e.AgentflowId,
            e.AgentflowRevision,
            e.Kind,
            e.RelateId,
        });
    }
}
