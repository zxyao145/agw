using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agentflows;

public class AgentflowConfiguration : IEntityTypeConfiguration<Agentflow>
{
    public void Configure(EntityTypeBuilder<Agentflow> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(1000);
        builder.Property(e => e.SystemPrompt).HasMaxLength(4000);
    }
}
