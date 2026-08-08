using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public sealed class AgentSessionStateEntryConfiguration :
    IEntityTypeConfiguration<AgentSessionStateEntry>
{
    public void Configure(EntityTypeBuilder<AgentSessionStateEntry> builder)
    {
        builder.HasKey(entry => new
        {
            entry.ProjectContextId,
            entry.AgentId,
            entry.AgentflowNodeId
        });
        builder.Property(entry => entry.AgentflowNodeId).HasMaxLength(512);
        builder.Property(entry => entry.SerializedSession).IsRequired();
        builder.HasIndex(entry => entry.UpdatedAt);

        builder.HasOne(entry => entry.ProjectContext)
            .WithMany()
            .HasForeignKey(entry => entry.ProjectContextId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(entry => entry.Agent)
            .WithMany()
            .HasForeignKey(entry => entry.AgentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
