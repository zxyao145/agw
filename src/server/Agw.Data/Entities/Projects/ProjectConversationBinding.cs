using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_conversation_binding")]
[EntityTypeConfiguration(typeof(ProjectConversationBindingConfiguration))]
public class ProjectConversationBinding : BaseEntity
{
    public Guid Id { get; set; }

    public Guid ProjectConversationId { get; set; }

    public ProjectConversation? ProjectConversation { get; set; }

    public Guid AgentId { get; set; }

    public string ExternalAgentName { get; set; } = string.Empty;

    public string ProviderSessionId { get; set; } = string.Empty;

    /// <summary>
    /// 这个外部会话已经看到的对话历史序号，用来给 External Agent 补它没见过的对话。
    /// The conversation history sequence this external session has already seen, used to give an External Agent the conversation it has not seen.
    /// </summary>
    public long? SeenThroughSequence { get; set; }
}
