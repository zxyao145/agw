using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("project_task_session_binding")]
public class ProjectTaskSessionBinding : BaseEntity
{
    public Guid Id { get; set; }

    public Guid TaskId { get; set; }

    public ProjectTask? Task { get; set; }

    public Guid AgentId { get; set; }

    public string ExternalAgentName { get; set; } = string.Empty;

    public string ProviderSessionId { get; set; } = string.Empty;
}
