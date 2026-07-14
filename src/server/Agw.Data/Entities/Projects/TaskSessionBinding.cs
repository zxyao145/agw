using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Data.Entities.Projects;

[Table("task_session_binding")]
public class TaskSessionBinding : BaseEntity
{
    public Guid Id { get; set; }

    public Guid ProjectContextId { get; set; }

    public ProjectContext? ProjectContext { get; set; }

    public Guid AgentId { get; set; }

    public string ExternalAgentName { get; set; } = string.Empty;

    public string ProviderSessionId { get; set; } = string.Empty;
}
