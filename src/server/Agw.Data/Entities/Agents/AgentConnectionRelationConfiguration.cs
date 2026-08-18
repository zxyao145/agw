using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public class AgentConnectionRelationConfiguration : IEntityTypeConfiguration<AgentConnectionRelation>
{
    public void Configure(EntityTypeBuilder<AgentConnectionRelation> builder)
    {
        builder.ToTable(table => table.HasComment("Binds an agent to an integration connection."));
        builder.HasKey(entity => new { entity.AgentId, entity.ConnectionId });

        builder
            .HasOne(entity => entity.Agent)
            .WithMany(agent => agent.AgentConnectionRelations)
            .HasForeignKey(entity => entity.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(entity => entity.Connection)
            .WithMany()
            .HasForeignKey(entity => entity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.ConnectionId);
    }
}
