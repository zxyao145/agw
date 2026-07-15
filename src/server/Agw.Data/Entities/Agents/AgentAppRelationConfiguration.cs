using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public class AgentAppRelationConfiguration : IEntityTypeConfiguration<AgentAppRelation>
{
    public void Configure(EntityTypeBuilder<AgentAppRelation> builder)
    {
        builder.HasKey(e => new { e.AgentId, e.AppInstanceId });

        builder.HasOne(e => e.Agent)
            .WithMany(agent => agent.AgentAppRelations)
            .HasForeignKey(e => e.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.AppInstance)
            .WithMany()
            .HasForeignKey(e => e.AppInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.AppInstanceId);
    }
}
