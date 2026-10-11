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
    /// 运行时只使用生效记录继续 provider session；同一绑定组最多一条生效记录，已归档记录保留供历史查询。
    /// The runtime continues the provider session only from the active record; a binding group has at most one active record, and archived records remain for history queries.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 这个外部会话已经看到的对话历史序号，用来给 External Agent 补它没见过的对话。
    /// The conversation history sequence this external session has already seen, used to give an External Agent the conversation it has not seen.
    /// </summary>
    public long? SeenThroughSequence { get; set; }
}
