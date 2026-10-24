using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Contracts.Conversations;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_conversation")]
[EntityTypeConfiguration(typeof(ProjectConversationConfiguration))]
public class ProjectConversation : BaseEntity
{
    public Guid Id { get; set; }

    [JsonIgnore]
    public int Generation { get; set; }

    public Guid ProjectId { get; set; }

    public Guid? JobId { get; set; }

    public string ContextId { get; set; } = string.Empty;

    public string Title { get; set; } = "Untitled";

    /// <summary>
    /// 创建方：<see cref="ConversationCreators.Host"/> 或创建对话的 Desktop extension 的 id。
    /// The creator: <see cref="ConversationCreators.Host"/> or the id of the Desktop extension that created the conversation.
    /// </summary>
    public string ExtensionId { get; set; } = ConversationCreators.Host;

    [JsonIgnore]
    public virtual Project? Project { get; set; }

    [JsonIgnore]
    public virtual ICollection<ProjectConversationChatHistory> ChatHistories { get; set; } =
        new List<ProjectConversationChatHistory>();

    [JsonIgnore]
    public virtual ICollection<ProjectConversationBinding> Bindings { get; set; } =
        new List<ProjectConversationBinding>();
}
