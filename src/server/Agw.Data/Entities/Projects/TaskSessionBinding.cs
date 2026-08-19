using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("task_session_binding")]
[EntityTypeConfiguration(typeof(TaskSessionBindingConfiguration))]
public class TaskSessionBinding : BaseEntity
{
    public Guid Id { get; set; }

    public Guid ProjectConversationId { get; set; }

    public ProjectConversation? ProjectConversation { get; set; }

    public Guid AgentId { get; set; }

    public string ExternalAgentName { get; set; } = string.Empty;

    public string ProviderSessionId { get; set; } = string.Empty;
}
