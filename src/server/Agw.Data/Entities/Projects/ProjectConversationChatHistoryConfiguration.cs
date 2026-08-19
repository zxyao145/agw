using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectConversationChatHistoryConfiguration : IEntityTypeConfiguration<ProjectConversationChatHistory>
{
    public void Configure(EntityTypeBuilder<ProjectConversationChatHistory> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ConversationId).HasColumnName("project_conversation_id");
        builder.Property(e => e.TaskId).IsRequired();
        builder.Property(e => e.JobId);
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.TaskErrorMessage).HasMaxLength(2000);
        builder.Property(e => e.AgentName).HasMaxLength(200);
        builder.Property(e => e.ConversationPayload).HasColumnType("text");
        builder.Property(e => e.Error).HasColumnType("text");
        builder
            .HasIndex(e => e.ConversationId)
            .HasDatabaseName("ix_project_conversation_chat_history_project_conversation_id");
        builder
            .HasIndex(e => new { e.ConversationId, e.ConversationSequence })
            .HasDatabaseName("ix_project_conversation_chat_history_project_conversation_id_conversation_sequence");
        builder.HasIndex(e => new { e.TaskId, e.CreateTime });
        builder.HasIndex(e => new { e.TaskId, e.ConversationSequence }).IsUnique(false);

        builder
            .HasOne(e => e.ProjectConversation)
            .WithMany(conversation => conversation.ChatHistories)
            .HasForeignKey(e => e.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .Property(e => e.Metadata)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v =>
                    string.IsNullOrWhiteSpace(v)
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v, (JsonSerializerOptions?)null)
            );
    }
}
