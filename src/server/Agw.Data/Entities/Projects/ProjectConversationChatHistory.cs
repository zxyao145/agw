using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

/// <summary>
/// 历史行的用途：用户输入、过程消息或 Result，以小写文本保存。
/// The purpose of a history row: user input, process message or Result, stored as lowercase text.
/// </summary>
public enum ConversationMessagePurpose
{
    Message = 0,
    Input = 1,
    Result = 2,
}

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

    /// <summary>
    /// 以下五列在插入时确定：所属 Turn、Turn 内的 Step（用户输入为 0，控制消息为空）、产生消息的 Agent、历史作用域与用途。
    /// The following five columns are fixed at insert: the owning turn, the Step within it (0 for user input, null for control messages), the producing Agent, the history scope and the purpose.
    /// </summary>
    public Guid? TurnId { get; set; }

    public int? StepIndex { get; set; }

    public Guid? AgentId { get; set; }

    public string? HistoryScope { get; set; }

    public ConversationMessagePurpose Purpose { get; set; }

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
