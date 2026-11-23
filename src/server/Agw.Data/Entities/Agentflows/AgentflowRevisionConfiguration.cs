using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agentflows;

public sealed class AgentflowRevisionConfiguration : IEntityTypeConfiguration<AgentflowRevision>
{
    public void Configure(EntityTypeBuilder<AgentflowRevision> builder)
    {
        builder.HasKey(revision => new { revision.AgentflowId, revision.Revision });
    }
}
