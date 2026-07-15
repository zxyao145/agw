using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Jobs;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Prompt).HasMaxLength(4000);
        builder.Property(e => e.TriggerType).HasConversion<int>();
        builder.Property(e => e.TriggerValue).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.LastError).HasMaxLength(2000);
        builder.Property(e => e.RowVersion)
            .IsRequired()
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(e => new { e.IsEnabled, e.Status, e.NextRunTime })
            .HasDatabaseName("ix_task_next_run_time");
        builder.HasIndex(e => e.ProjectId)
            .HasDatabaseName("ix_task_project");
    }
}
