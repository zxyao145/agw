using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("task_session_binding")]
public class TaskSessionBinding : BaseEntity
{
    public Guid Id { get; set; }

    public Guid TaskId { get; set; }

    public Guid AgentId { get; set; }

    public string ExternalAgentName { get; set; } = string.Empty;

    public string ProviderSessionId { get; set; } = string.Empty;
}
