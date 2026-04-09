using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("project_task_record")]
public class TaskRecord
{
    public Guid Id { get; set; }

    /// <summary>
    /// unified as ProjectTask.Id
    /// </summary>
    public Guid TaskId { get; set; }

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
