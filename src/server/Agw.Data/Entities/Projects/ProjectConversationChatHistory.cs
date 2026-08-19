using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_conversation_chat_history")]
[EntityTypeConfiguration(typeof(ProjectConversationChatHistoryConfiguration))]
public class ProjectConversationChatHistory
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid TaskId { get; set; }

    public Guid? JobId { get; set; }

    public TaskExecutionStatus Status { get; set; } = TaskExecutionStatus.Pending;

    public DateTimeOffset? FinishedTime { get; set; }

    public string? TaskErrorMessage { get; set; }

    public string? AgentName { get; set; }

    public long? ConversationSequence { get; set; }

    public string? ConversationPayload { get; set; }

    ///// <summary>
    ///// User input to be executed by the associated target.
    ///// </summary>
    //public UserInputMessage? Input { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreateTime { get; set; }
    public DateTimeOffset? UpdateTime { get; set; }

    [JsonIgnore]
    public virtual ProjectConversation? ProjectConversation { get; set; }
}
