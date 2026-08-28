using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Jobs;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable(
            "job",
            table =>
                table.HasCheckConstraint(
                    "ck_job_active_attempt",
                    "(status = 2 AND active_execution_id IS NOT NULL AND active_attempt_started_at IS NOT NULL) "
                        + "OR (status <> 2 AND active_execution_id IS NULL AND active_attempt_started_at IS NULL)"
                )
        );
        builder.HasKey(e => e.Id);
        builder.Property(e => e.CreateBy).IsRequired();
        builder
            .HasMany(e => e.Logs)
            .WithOne(log => log.Job)
            .HasForeignKey(log => log.JobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Prompt).HasMaxLength(4000);
        builder.Property(e => e.TriggerType).HasConversion<int>();
        builder.Property(e => e.TriggerValue).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.LastError).HasMaxLength(2000);
        builder.Property(e => e.ActiveExecutionId).HasColumnName("active_execution_id");
        builder.Property(e => e.ActiveAttemptStartedAt).HasColumnName("active_attempt_started_at");
        builder.Property(e => e.RowVersion).IsRequired().IsConcurrencyToken().ValueGeneratedNever();
        builder.HasIndex(e => e.ActiveExecutionId).IsUnique();

        builder
            .HasIndex(e => new
            {
                e.IsEnabled,
                e.Status,
                e.NextRunTime,
            })
            .HasDatabaseName("ix_task_next_run_time");
        builder.HasIndex(e => e.ProjectId).HasDatabaseName("ix_task_project");
    }
}
