using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public sealed class BackgroundExecutionConfiguration : IEntityTypeConfiguration<BackgroundExecution>
{
    public void Configure(EntityTypeBuilder<BackgroundExecution> builder)
    {
        builder.HasKey(execution => execution.Id);
        builder.HasIndex(execution => new
        {
            execution.CreateBy,
            execution.CallerKey,
            execution.CorrelationKey,
        });
        builder.HasIndex(execution => execution.State);
        builder.Property(execution => execution.CreateBy).IsRequired();
        builder.Property(execution => execution.CallerKey).IsRequired();
        builder.Property(execution => execution.CorrelationKey).IsRequired();
        builder.Property(execution => execution.RequestHash).HasMaxLength(64);
    }
}
