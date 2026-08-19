using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agentflows;

public class AgentflowTraceConfiguration : IEntityTypeConfiguration<AgentflowTrace>
{
    public void Configure(EntityTypeBuilder<AgentflowTrace> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
        builder.Property(e => e.NodeId).IsRequired().HasMaxLength(200);
        builder.Property(e => e.NodeName).HasMaxLength(200);
        builder.Property(e => e.NodeKind).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.AgentName).HasMaxLength(200);
        builder.Property(e => e.Input).IsRequired().HasColumnType("text");
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.Error).HasColumnType("text");
        builder.HasIndex(e => new
        {
            e.ProjectId,
            e.ContextId,
            e.TaskId,
            e.StartTimeUtc,
        });
        builder.HasIndex(e => new
        {
            e.AgentflowId,
            e.NodeId,
            e.StartTimeUtc,
        });
    }
}
