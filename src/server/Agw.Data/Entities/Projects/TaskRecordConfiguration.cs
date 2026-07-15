using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class TaskRecordConfiguration : IEntityTypeConfiguration<TaskRecord>
{
    public void Configure(EntityTypeBuilder<TaskRecord> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.TaskId).IsRequired();
        builder.Property(e => e.JobId);
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.TaskErrorMessage).HasMaxLength(2000);
        builder.Property(e => e.AgentName).HasMaxLength(200);
        builder.Property(e => e.ConversationPayload).HasColumnType("text");
        builder.Property(e => e.Error).HasColumnType("text");
        builder.HasIndex(e => e.ProjectContextId);
        builder.HasIndex(e => new { e.ProjectContextId, e.ConversationSequence });
        builder.HasIndex(e => new { e.TaskId, e.CreateTime });
        builder.HasIndex(e => new { e.TaskId, e.ConversationSequence }).IsUnique(false);

        builder.HasOne(e => e.ProjectContext)
            .WithMany(context => context.Records)
            .HasForeignKey(e => e.ProjectContextId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(e => e.Metadata)
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v,
                        (JsonSerializerOptions?)null));
    }
}
