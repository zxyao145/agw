using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

using Agw.Shared.Contracts.Tasks;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("project_task_record")]
public class TaskRecord
{
    public Guid Id { get; set; }

    public Guid ProjectContextId { get; set; }

    [JsonIgnore]
    public ProjectContext? ProjectContext { get; set; }

    public Guid TaskId { get; set; }

    public Guid? JobId { get; set; }

    public TaskExecutionStatus Status { get; set; } = TaskExecutionStatus.Pending;

    public DateTime? FinishedTime { get; set; }

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

    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}
