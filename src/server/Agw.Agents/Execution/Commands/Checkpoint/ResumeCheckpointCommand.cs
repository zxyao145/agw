using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Checkpoint;

/// <summary>
/// 从一次精确的 Agentflow checkpoint occurrence 创建新的执行分支。
/// </summary>
public sealed class ResumeCheckpointCommand : AgentRunCommand
{
    [JsonConstructor]
    [SetsRequiredMembers]
    public ResumeCheckpointCommand(
        Guid checkpointOccurrenceId,
        Guid resumeExecutionId,
        Guid agentflowId)
    {
        CheckpointOccurrenceId = checkpointOccurrenceId;
        ResumeExecutionId = resumeExecutionId;
        AgentflowId = agentflowId;
    }

    public Guid CheckpointOccurrenceId { get; set; }

    public Guid ResumeExecutionId { get; set; }

    public Guid AgentflowId { get; set; }
}
