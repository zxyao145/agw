using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public class AgentMcpServerRelationConfiguration : IEntityTypeConfiguration<AgentMcpServerRelation>
{
    public void Configure(EntityTypeBuilder<AgentMcpServerRelation> builder)
    {
        builder.HasKey(e => new { e.AgentId, e.McpToolServerId });

        builder
            .HasOne(e => e.Agent)
            .WithMany(a => a.AgentMcpToolServers)
            .HasForeignKey(e => e.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(e => e.McpToolServer)
            .WithMany(s => s.AgentMcpToolServers)
            .HasForeignKey(e => e.McpToolServerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.McpToolServerId);
    }
}
