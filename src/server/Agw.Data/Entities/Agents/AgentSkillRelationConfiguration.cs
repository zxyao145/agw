using Agw.Shared.Data.Entities.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public class AgentSkillRelationConfiguration : IEntityTypeConfiguration<AgentSkillRelation>
{
    public void Configure(EntityTypeBuilder<AgentSkillRelation> builder)
    {
        builder.HasKey(e => new { e.AgentId, e.SkillId });

        builder
            .HasOne(e => e.Agent)
            .WithMany(a => a.AgentSkillRelations)
            .HasForeignKey(e => e.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Skill>().WithMany().HasForeignKey(e => e.SkillId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.SkillId);
    }
}
