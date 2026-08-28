using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class AgentUsageConfiguration : IEntityTypeConfiguration<AgentUsage>
{
    public void Configure(EntityTypeBuilder<AgentUsage> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(128);
        builder.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
        builder.Property(e => e.AgentName).IsRequired().HasMaxLength(200);

        builder.HasIndex(e => e.UserId);
        builder.HasIndex(e => new { e.ProjectId, e.ContextId });
        builder.HasIndex(e => e.AgentName);
        builder.HasIndex(e => e.RecordedAt);
    }
}
