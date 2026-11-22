using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agentflows;

public sealed class AgentflowBlockMemberConfiguration : IEntityTypeConfiguration<AgentflowBlockMember>
{
    public void Configure(EntityTypeBuilder<AgentflowBlockMember> builder)
    {
        builder.HasKey(member => new
        {
            member.AgentflowId,
            member.AgentflowRevision,
            member.NodeId,
            member.MemberId,
        });
        builder.Property(member => member.Kind).HasConversion<int>();
        builder.Property(member => member.NodeId).HasMaxLength(200).IsRequired();
        builder.Property(member => member.MemberId).HasMaxLength(64).IsRequired();
        builder.Property(member => member.Name).HasMaxLength(200).IsRequired();
        builder.Property(member => member.Instructions).HasMaxLength(8000);
        builder.HasIndex(member => new
        {
            member.AgentflowId,
            member.AgentflowRevision,
            member.Kind,
            member.RelateId,
        });
    }
}
