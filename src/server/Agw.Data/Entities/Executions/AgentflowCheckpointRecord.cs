using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// Agentflow 在一个 MAF superstep 中产生的可恢复 checkpoint occurrence。
/// 同一 superstep 的多个 CheckpointMarker 共享该记录和聊天历史边界。
/// </summary>
[Table("agentflow_checkpoint")]
[EntityTypeConfiguration(typeof(AgentflowCheckpointRecordConfiguration))]
public sealed class AgentflowCheckpointRecord : BaseEntity
{
    public Guid Id { get; set; }

    public Guid? SourceExecutionId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid ProjectConversationId { get; set; }

    public string ContextId { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public Guid AgentflowId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public bool IsDurable { get; set; }

    public long BoundarySequence { get; set; }

    public string DefinitionFingerprint { get; set; } = string.Empty;

    public string MarkersJson { get; set; } = "[]";

    [Encrypted]
    public string CheckpointJson { get; set; } = string.Empty;
}
