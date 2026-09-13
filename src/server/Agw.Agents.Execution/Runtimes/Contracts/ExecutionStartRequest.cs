using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Shared.Runtime;

namespace Agw.Agents.Execution.Runtimes.Contracts;

/// <summary>
/// 已解析的执行输入。运行时实例、消息通道和 Host 生命周期由实现持有。
/// </summary>
internal sealed record ExecutionStartRequest(
    Guid ExecutionId,
    ExecutionTarget Target,
    AgentExecutionTask Task,
    ExecutionSettings Settings,
    AgwUserInput Input,
    bool Stream,
    string Workspace
)
{
    public ProjectWorkspaceSnapshot? WorkspaceSnapshot { get; init; }

    public string? RequestedMode { get; init; }

    public AgentflowCheckpointSnapshot? ResumeCheckpoint { get; init; }
}
